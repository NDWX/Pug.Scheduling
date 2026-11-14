using System.Data;
using System.Data.Common;
using System.Transactions;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using static System.String;

namespace Pug.Scheduling.Data.SQLite
{
	// The schedule store: schedule + run-history tables. The execution lease of a schedule's active
	// run lives on the schedule row (schema v3). DateTimes are stored as invariant ISO-8601 TEXT
	// (chronologically comparable); durations as whole seconds.
	public class DapperData : ApplicationData<IDataSession>
	{
		public DapperData( string location, DbProviderFactory dataProvider ) : base( location, dataProvider )
		{
		}

		// A session created inside a unit of work's scope enlists in it itself: Microsoft.Data.Sqlite
		// would otherwise autocommit every statement (see DapperDataSession).
		protected override IDataSession CreateApplicationDataSession( IDbConnection databaseSession, DbProviderFactory dataAccessProvider )
		{
			DapperDataSession session = new( databaseSession );

			if( Transaction.Current is not null )
				session.EnlistInAmbientTransaction( Transaction.Current );

			return session;
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
											    identifier text not null primary key,
											    domain text not null,
											    ownerType text not null,
											    owner text not null,
											    name text not null,
											    jobType text not null,
											    payload text,
											    cronExpression text,
											    intervalSeconds integer,
											    timeZone text not null,
											    notBefore text,
											    notAfter text,
											    misfireHandling text not null,
											    misfireThresholdSeconds integer not null,
											    maxAttempts integer not null,
											    retryBackoffSeconds integer not null,
											    timeoutSeconds integer not null,
											    enabled integer not null,
											    paused integer not null,
											    nextRun text,
											    runRequested integer not null default 0,
											    attempt integer not null,
											    lastRun text,
											    registration text not null,
											    lastUpdate text
											);
											create index ixScheduleDue on schedule(nextRun) where enabled = 1 and paused = 0;
											create index ixScheduleDomain on schedule(domain, owner);",
									@"drop table schedule;"
								),
							new UpgradeScript(
									"Schedule run table", Empty,
									@"create table scheduleRun(
											    identifier text not null primary key,
											    schedule text not null,
											    domain text not null,
											    scheduledFor text not null,
											    started text not null,
											    finished text,
											    attempt integer not null,
											    runTrigger text not null,
											    result text,
											    outcome text
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
									@"create index ixScheduleJobTypeDue on schedule(jobType, nextRun) where enabled = 1 and paused = 0;",
									@"drop index ixScheduleJobTypeDue;"
								),
							new UpgradeScript(
									"Schedule table job-type due index — run claimant", Empty,
									@"alter table scheduleRun add column claimedBy text;",
									@"alter table scheduleRun drop column claimedBy;"
								)
						}
					),
				new SchemaVersion(
						3, new[]
						{
							// The execution lease moves onto the schedule row (it had its own store),
							// with the retried occurrence; the due indexes now exclude schedules with
							// a run in progress.
							new UpgradeScript(
									"Schedule table v3 execution state", Empty,
									@"alter table schedule add column occurrence text;
											alter table schedule add column activeRun text;
											alter table schedule add column holder text;
											alter table schedule add column leaseExpiry text;
											drop index ixScheduleDue;
											drop index ixScheduleJobTypeDue;
											create index ixScheduleDue on schedule(nextRun)
												where enabled = 1 and paused = 0 and activeRun is null;
											create index ixScheduleJobTypeDue on schedule(jobType, nextRun)
												where enabled = 1 and paused = 0 and activeRun is null;
											create index ixScheduleRequested on schedule(identifier)
												where enabled = 1 and paused = 0 and activeRun is null and runRequested = 1;
											create index ixScheduleActive on schedule(activeRun) where activeRun is not null;",
									@"drop index ixScheduleActive;
											drop index ixScheduleRequested;
											drop index ixScheduleJobTypeDue;
											drop index ixScheduleDue;
											create index ixScheduleDue on schedule(nextRun) where enabled = 1 and paused = 0;
											create index ixScheduleJobTypeDue on schedule(jobType, nextRun) where enabled = 1 and paused = 0;
											alter table schedule drop column leaseExpiry;
											alter table schedule drop column holder;
											alter table schedule drop column activeRun;
											alter table schedule drop column occurrence;"
								),
							// A pending retry keeps the occurrence of its latest run. The latest
							// unfinished run becomes the active run, lapsed (expiry at the epoch),
							// so the next claim adopts it; its run request, raised during the run,
							// is taken as satisfied by it.
							new UpgradeScript(
									"Schedule table v3 execution state backfill", Empty,
									@"update schedule set occurrence = (
												select run.scheduledFor from scheduleRun run
												where run.schedule = schedule.identifier
												order by run.started desc limit 1)
											where attempt > 0;
											update schedule set activeRun = (
												select run.identifier from scheduleRun run
												where run.schedule = schedule.identifier and run.finished is null
												order by run.started desc limit 1);
											update schedule set
												holder = (select run.claimedBy from scheduleRun run where run.identifier = schedule.activeRun),
												leaseExpiry = '1970-01-01 00:00:00',
												runRequested = 0
											where activeRun is not null;",
									Empty
								)
						}
					)
			};
		}
	}
}
