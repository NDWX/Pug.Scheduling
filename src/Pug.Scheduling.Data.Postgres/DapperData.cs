using System.Data;
using System.Data.Common;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using static System.String;

namespace Pug.Scheduling.Data.Postgres
{
	// The schedule store: schedule + run-history tables. Carries no lease columns — execution
	// leases live in the lease provider's own store. Durations are stored as whole seconds.
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
					)
			};
		}
	}
}
