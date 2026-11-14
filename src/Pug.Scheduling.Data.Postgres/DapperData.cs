using System.Data;
using System.Data.Common;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using static System.String;

namespace Pug.Scheduling.Data.Postgres
{
	// The schedule store: schedule + run-history tables. The execution lease of a schedule's active
	// run lives on the schedule row (schema v3). Durations are stored as whole seconds.
	public class DapperData : ApplicationData<IDataSession>
	{
		public DapperData( string location, DbProviderFactory dataProvider ) : base( location, dataProvider )
		{
		}

		protected override IDataSession CreateApplicationDataSession( IDbConnection databaseSession, DbProviderFactory dataAccessProvider )
		{
			return new DapperDataSession( databaseSession );
		}

		protected override IEnumerable<SchemaVersion> InitializeUpgradeScripts()
		{
			return new[]
			{
				new SchemaVersion(
						1, new[]
						{
							new UpgradeScript(
									"Schedule table", Empty,
									@"create table schedule(
											    identifier character varying not null primary key,
											    domain character varying not null,
											    ownerType character varying not null,
											    owner character varying not null,
											    name character varying not null,
											    jobType character varying not null,
											    payload character varying,
											    cronExpression character varying,
											    intervalSeconds bigint,
											    timeZone character varying not null,
											    notBefore timestamp with time zone,
											    notAfter timestamp with time zone,
											    misfireHandling character varying not null,
											    misfireThresholdSeconds bigint not null,
											    maxAttempts integer not null,
											    retryBackoffSeconds bigint not null,
											    timeoutSeconds bigint not null,
											    enabled boolean not null,
											    paused boolean not null,
											    nextRun timestamp with time zone,
											    runRequested boolean not null default false,
											    attempt integer not null,
											    lastRun character varying,
											    registration character varying not null,
											    lastUpdate character varying
											);
											create index ixScheduleDue on schedule(nextRun) where enabled and not paused;
											create index ixScheduleDomain on schedule(domain, owner);",
									@"drop table schedule;"
								),
							new UpgradeScript(
									"Schedule run table", Empty,
									@"create table scheduleRun(
											    identifier character varying not null primary key,
											    schedule character varying not null,
											    domain character varying not null,
											    scheduledFor timestamp with time zone not null,
											    started timestamp with time zone not null,
											    finished timestamp with time zone,
											    attempt integer not null,
											    runTrigger character varying not null,
											    result character varying,
											    outcome character varying
											);
											create unique index uxScheduleRunFire on scheduleRun(schedule, scheduledFor, attempt);
											create index ixScheduleRunSchedule on scheduleRun(schedule, started desc);",
									@"drop table scheduleRun;"
								)
						}
					),
				new SchemaVersion(
						2, new[]
						{
							// Serves claims restricted to one job type: the due index alone would
							// scan every due schedule of other types to find them.
							new UpgradeScript(
									"Schedule table job-type due index", Empty,
									@"create index ixScheduleJobTypeDue on schedule(jobType, nextRun) where enabled and not paused;",
									@"drop index ixScheduleJobTypeDue;"
								),
							new UpgradeScript(
									"Schedule table job-type due index — run claimant", Empty,
									@"alter table scheduleRun add column claimedBy character varying;",
									@"alter table scheduleRun drop column claimedBy;"
								)
						}
					),
				new SchemaVersion(
						3, new[]
						{
							// The execution lease moves onto the schedule row (it had its own store),
							// with the retried occurrence; the due indexes now exclude schedules with
							// a run in progress. leaseExpiry is in no index, so a heartbeat is a HOT
							// update; the fill factor leaves the page room for one.
							new UpgradeScript(
									"Schedule table v3 execution state", Empty,
									@"alter table schedule
												add column occurrence timestamp with time zone,
												add column activeRun character varying,
												add column holder character varying,
												add column leaseExpiry timestamp with time zone;
											alter table schedule set (fillfactor = 90);
											drop index ixScheduleDue;
											drop index ixScheduleJobTypeDue;
											create index ixScheduleDue on schedule(nextRun)
												where enabled and not paused and activeRun is null;
											create index ixScheduleJobTypeDue on schedule(jobType, nextRun)
												where enabled and not paused and activeRun is null;
											create index ixScheduleRequested on schedule(identifier)
												where enabled and not paused and activeRun is null and runRequested;
											create index ixScheduleActive on schedule(activeRun) where activeRun is not null;",
									@"drop index ixScheduleActive;
											drop index ixScheduleRequested;
											drop index ixScheduleJobTypeDue;
											drop index ixScheduleDue;
											create index ixScheduleDue on schedule(nextRun) where enabled and not paused;
											create index ixScheduleJobTypeDue on schedule(jobType, nextRun) where enabled and not paused;
											alter table schedule reset (fillfactor);
											alter table schedule
												drop column leaseExpiry,
												drop column holder,
												drop column activeRun,
												drop column occurrence;"
								),
							// A pending retry keeps the occurrence of its latest run. The latest
							// unfinished run becomes the active run, lapsed (expiry at the epoch),
							// so the next claim adopts it; its run request, raised during the run,
							// is taken as satisfied by it.
							new UpgradeScript(
									"Schedule table v3 execution state backfill", Empty,
									@"update schedule set occurrence = latest.scheduledFor
											from (select distinct on (schedule) schedule, scheduledFor from scheduleRun
												order by schedule, started desc) latest
											where latest.schedule = schedule.identifier and schedule.attempt > 0;
											update schedule set activeRun = unfinished.identifier, holder = unfinished.claimedBy,
												leaseExpiry = 'epoch', runRequested = false
											from (select distinct on (schedule) schedule, identifier, claimedBy from scheduleRun
												where finished is null order by schedule, started desc) unfinished
											where unfinished.schedule = schedule.identifier;",
									Empty
								)
						}
					)
			};
		}
	}
}
