using System.Data.Common;
using Dapper;
using DbUp;
using DbUp.Engine;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pug.Application.Data;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// A schema-v2 store, with the execution state v2 kept outside the schedule row, upgrades to v3
// under DbUp: the retried occurrence and each schedule's unfinished run are carried onto the row,
// and the next claim adopts that run rather than recording its occurrence again.
public abstract class ScheduleStoreMigrationTests
{
	private static readonly DateTime Now = new( 2031, 1, 2, 3, 0, 0, DateTimeKind.Utc );
	private static readonly DateTime Earlier = Now.AddHours( -2 );
	private static readonly DateTime Latest = Now.AddHours( -1 );

	protected abstract Task<string> CreateDatabaseAsync();

	protected abstract Task DropDatabaseAsync( string connectionString );

	protected abstract ApplicationData<IDataSession> CreateStore( string connectionString );

	protected abstract void Upgrade( string connectionString, IEnumerable<SchemaUpgradeScript> scripts );

	protected abstract DbConnection Connect( string connectionString );

	[Fact]
	public async Task V2DatabaseMigratesToV3WithBackfill()
	{
		string connectionString = await CreateDatabaseAsync();

		try
		{
			ApplicationData<IDataSession> store = CreateStore( connectionString );
			SchemaUpgradeScript[] scripts = store.GetSchemaUpgradeScripts().ToArray();

			Upgrade( connectionString, scripts.Where( script => script.SchemaVersion <= 2 ) );

			await using( DbConnection connection = Connect( connectionString ) )
			{
				await connection.OpenAsync();

				// Idle: one finished run.
				await InsertV2ScheduleAsync( connection, "idle", attempt: 0, nextRun: Now.AddHours( 1 ), runRequested: false );
				await InsertV2RunAsync( connection, "idle-run", "idle", Earlier, 0, finished: true, claimedBy: "w/1" );

				// A retry pending: attempt 1 of the occurrence its latest run executed.
				await InsertV2ScheduleAsync( connection, "retrying", attempt: 1, nextRun: Now.AddMinutes( 1 ), runRequested: false );
				await InsertV2RunAsync( connection, "retrying-run", "retrying", Earlier, 0, finished: true, claimedBy: "w/1" );

				// A run in progress, with a run-now raised during it (v2 moved nextRun for it).
				await InsertV2ScheduleAsync( connection, "running", attempt: 0, nextRun: Now, runRequested: true );
				await InsertV2RunAsync( connection, "running-old", "running", Earlier, 0, finished: true, claimedBy: "w/1" );
				await InsertV2RunAsync( connection, "running-run", "running", Latest, 0, finished: false, claimedBy: "w/2" );

				// A run in progress recorded before runs carried their holder.
				await InsertV2ScheduleAsync( connection, "legacy", attempt: 0, nextRun: Earlier, runRequested: false );
				await InsertV2RunAsync( connection, "legacy-run", "legacy", Earlier, 0, finished: false, claimedBy: null );
			}

			Upgrade( connectionString, scripts );

			ScheduleInfo idle = await ReadAsync( store, "idle" );

			Assert.Equal( 0, idle.State.Attempt );
			Assert.Null( idle.State.Occurrence );
			Assert.Null( idle.ExecutionLease );

			ScheduleInfo retrying = await ReadAsync( store, "retrying" );

			Assert.Equal( 1, retrying.State.Attempt );
			Assert.Equal( Earlier, retrying.State.Occurrence );
			Assert.Equal( DateTimeKind.Utc, retrying.State.Occurrence!.Value.Kind );
			Assert.Null( retrying.ExecutionLease );

			ScheduleInfo running = await ReadAsync( store, "running" );

			Assert.Equal( new ExecutionLease { Run = "running-run", ClaimedBy = "w/2", Expiry = DateTime.UnixEpoch },
						running.ExecutionLease );
			Assert.Null( running.State.Occurrence );
			Assert.False( running.State.RunRequested );

			ScheduleInfo legacy = await ReadAsync( store, "legacy" );

			Assert.Equal( "legacy-run", legacy.ExecutionLease.Run );
			Assert.Null( legacy.ExecutionLease.ClaimedBy );

			// The lapsed run is adopted by the next claim: same run, no second record.
			ExecutionCoordinator coordinator = new(
					new SequentialIdGenerator( "migrated" ), store, SecurityManagerFactory.CreateWithoutPrincipal(),
					new ExecutionCoordinatorOptions(), new TimeProviderClock( new ManualTimeProvider( Now ) ),
					NullLogger<ExecutionCoordinator>.Instance );

			ClaimedJob? job = await coordinator.ClaimDueJobAsync( "upgraded", 1, "running-type" );

			Assert.Equal( "running-run", job?.Context.Run );
			Assert.Equal( Latest, job!.Context.ScheduledFor );
			Assert.Equal( 2, ( await store.ReadAsync( ( session, context ) => session.GetRunsAsync( context ), "running" ) )
							.Count() );
		}
		finally
		{
			await DropDatabaseAsync( connectionString );
		}
	}

	private static async Task<ScheduleInfo> ReadAsync( ApplicationData<IDataSession> store, string identifier ) =>
		( await store.ReadAsync( ( session, context ) => session.GetScheduleAsync( context ), identifier ) )!;

	// The v2 schedule row, as a v2 store wrote it; each schedule has a job type of its own.
	private static Task InsertV2ScheduleAsync( DbConnection connection, string identifier, int attempt, DateTime? nextRun,
												bool runRequested ) =>
		connection.ExecuteAsync(
				@"insert into schedule(identifier, domain, ownerType, owner, name, jobType, payload, cronExpression,
						intervalSeconds, timeZone, notBefore, notAfter, misfireHandling, misfireThresholdSeconds, maxAttempts,
						retryBackoffSeconds, timeoutSeconds, enabled, paused, nextRun, runRequested, attempt, lastRun,
						registration, lastUpdate)
					values(@identifier, 'tenant-1', 'USER', 'owner-1', @identifier, @jobType, null, null, 3600, 'Etc/UTC',
						null, null, 'SKIP', 300, 3, 60, 600, @enabled, @paused, @nextRun, @runRequested, @attempt, null,
						@registration, null);",
				new
				{
					identifier,
					jobType = $"{identifier}-type",
					enabled = true,
					paused = false,
					nextRun,
					runRequested,
					attempt,
					registration = /*lang=json*/ """{"Actor":{"Type":"USER","Identifier":"owner-1"},"Timestamp":"2031-01-01T00:00:00Z"}"""
				} );

	private static Task InsertV2RunAsync( DbConnection connection, string identifier, string schedule, DateTime scheduledFor,
										int attempt, bool finished, string? claimedBy ) =>
		connection.ExecuteAsync(
				@"insert into scheduleRun(identifier, schedule, domain, scheduledFor, started, finished, attempt, runTrigger,
						result, outcome, claimedBy)
					values(@identifier, @schedule, 'tenant-1', @scheduledFor, @scheduledFor, @finishedAt, @attempt, 'SCHEDULE',
						@result, null, @claimedBy);",
				new
				{
					identifier,
					schedule,
					scheduledFor,
					finishedAt = finished ? scheduledFor.AddMinutes( 1 ) : (DateTime?)null,
					attempt,
					result = finished ? RunResults.Failed : null,
					claimedBy
				} );
}

public sealed class SqliteScheduleStoreMigrationTests : ScheduleStoreMigrationTests
{
	protected override Task<string> CreateDatabaseAsync() =>
		Task.FromResult(
				$"Data Source={Path.Combine( Path.GetTempPath(), $"pug_scheduling_migration_{Guid.NewGuid():N}.db" )};Pooling=False;" );

	protected override Task DropDatabaseAsync( string connectionString )
	{
		string path = new SqliteConnectionStringBuilder( connectionString ).DataSource;

		if( File.Exists( path ) )
			File.Delete( path );

		return Task.CompletedTask;
	}

	protected override ApplicationData<IDataSession> CreateStore( string connectionString ) =>
		new Data.SQLite.DapperData( connectionString, SqliteFactory.Instance );

	protected override void Upgrade( string connectionString, IEnumerable<SchemaUpgradeScript> scripts ) =>
		SchemaUpgrade.Apply( DeployChanges.To.SqliteDatabase( connectionString ), scripts );

	protected override DbConnection Connect( string connectionString ) => new SqliteConnection( connectionString );
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresScheduleStoreMigrationTests : ScheduleStoreMigrationTests
{
	protected override Task<string> CreateDatabaseAsync() => PostgresTestServer.CreateDatabaseAsync( "migration" );

	protected override Task DropDatabaseAsync( string connectionString ) =>
		PostgresTestServer.DropDatabaseAsync( connectionString );

	protected override ApplicationData<IDataSession> CreateStore( string connectionString ) =>
		new Data.Postgres.DapperData( connectionString, NpgsqlFactory.Instance );

	protected override void Upgrade( string connectionString, IEnumerable<SchemaUpgradeScript> scripts ) =>
		PostgresTestServer.UpgradeDatabase( connectionString, scripts );

	protected override DbConnection Connect( string connectionString ) => new NpgsqlConnection( connectionString );
}
