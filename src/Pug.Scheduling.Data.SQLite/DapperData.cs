using System.Data;
using System.Data.Common;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using static System.String;

namespace Pug.Scheduling.Data.SQLite
{
	// The schedule store: schedule + run-history tables. Carries no lease columns — execution
	// leases live in the lease provider's own store. DateTimes are stored as invariant ISO-8601
	// TEXT (chronologically comparable); durations as whole seconds.
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
					)
			};
		}
	}
}
