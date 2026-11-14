using Pug.Effable;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// A UnitsOfWork unit is one local transaction on every provider: its writes commit or roll back
// together, and a write unit that reads and then writes is not overtaken by a concurrent one. On
// SQLite both hold only because the session enlists itself in the unit's scope (Microsoft.Data.Sqlite
// ignores ambient transactions); Npgsql enlists natively.
public abstract class ScheduleStoreUnitOfWorkTests<TScheduleDatabase>
	where TScheduleDatabase : class, IScheduleDatabaseFixture
{
	private static readonly RunSummary LastRun = new()
	{
		Started = new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc ),
		Finished = new DateTime( 2030, 1, 1, 0, 1, 0, DateTimeKind.Utc ),
		Result = RunResults.Succeeded
	};

	private readonly TScheduleDatabase _database;

	protected ScheduleStoreUnitOfWorkTests( TScheduleDatabase database )
	{
		_database = database;
	}

	private static ScheduleInfo NewSchedule() =>
		new()
		{
			Identifier = $"schedule-{Guid.NewGuid():N}",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			Definition = new ScheduleDefinition
			{
				Domain = "tenant-1",
				Name = "Unit of work test schedule",
				JobType = "test",
				Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" },
				MisfirePolicy = new MisfirePolicy
				{
					Handling = MisfireHandlings.FireOnce,
					Threshold = TimeSpan.FromMinutes( 5 )
				},
				RetryPolicy = new RetryPolicy { MaximumAttempts = 3, Backoff = TimeSpan.FromMinutes( 1 ) },
				Timeout = TimeSpan.FromMinutes( 10 )
			},
			State = new ScheduleState
			{
				Enabled = true,
				NextRun = new DateTime( 2030, 1, 1, 1, 0, 0, DateTimeKind.Utc ),
				Attempt = 0
			},
			Registration = new ActionContext<Reference>
			{
				Actor = new Reference { Type = "USER", Identifier = "owner-1" },
				Timestamp = new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc )
			}
		};

	private static ScheduleRun NewRun( ScheduleInfo schedule ) =>
		new()
		{
			Identifier = $"run-{Guid.NewGuid():N}",
			Schedule = schedule.Identifier,
			Domain = schedule.Definition.Domain,
			ScheduledFor = schedule.State.NextRun!.Value,
			Started = schedule.State.NextRun!.Value,
			Attempt = 0,
			Trigger = RunTriggers.Schedule
		};

	private Task InsertAsync( ScheduleInfo schedule ) =>
		_database.ApplicationData.WriteAsync( ( session, context ) => session.InsertAsync( context ), schedule );

	private Task<ScheduleInfo?> ReadScheduleAsync( string identifier ) =>
		_database.ApplicationData.ReadAsync( ( session, context ) => session.GetScheduleAsync( context ), identifier );

	[Fact]
	public async Task StoreUnitIsAtomic()
	{
		ScheduleInfo schedule = NewSchedule();

		await Assert.ThrowsAsync<InvalidOperationException>(
				() => _database.ApplicationData.WriteAsync(
						async ( session, context ) =>
						{
							await session.InsertAsync( context );
							await session.InsertRunAsync( NewRun( context ) );

							throw new InvalidOperationException( "The unit fails after its writes." );
						},
						schedule ) );

		Assert.Null( await ReadScheduleAsync( schedule.Identifier ) );

		Assert.Empty(
				await _database.ApplicationData.ReadAsync(
						( session, context ) => session.GetRunsAsync( context ), schedule.Identifier ) );
	}

	// Sal's ConcurrentTransactionsGetDistinctVersions: every unit reads the counter (with the row
	// lock a unit acting on its read takes) and writes it back incremented, so only serialization
	// of the units makes every value distinct and the final value the number of units.
	[Fact]
	public async Task ConcurrentWriteUnitsGetDistinctValues()
	{
		const int units = 8;

		ScheduleInfo schedule = NewSchedule();

		await InsertAsync( schedule );

		Task<int>[] increments = Enumerable.Range( 0, units )
										.Select( _ => Task.Run( () => IncrementAttemptAsync( schedule.Identifier ) ) )
										.ToArray();

		int[] written = await Task.WhenAll( increments );

		Assert.Equal( Enumerable.Range( 1, units ), written.Order() );
		Assert.Equal( units, ( await ReadScheduleAsync( schedule.Identifier ) )!.State.Attempt );
	}

	private Task<int> IncrementAttemptAsync( string identifier ) =>
		_database.ApplicationData.WriteAsync(
				async ( session, context ) =>
				{
					ScheduleInfo current = ( await session.GetScheduleAsync( context, lockRow: true ) )!;

					int next = current.State.Attempt + 1;

					// Widens the window between the read and the write a lost update needs.
					await Task.Delay( 20 );

					await session.RecordOccurrenceCompletionAsync( context, next, current.State.NextRun, LastRun );

					return next;
				},
				identifier );

	// A read unit takes no write lock: a write to the same schedule completes while a read is
	// open, and the read keeps seeing the snapshot it started with (Sal's AnOpenReadDoesNotBlockAWrite).
	[Fact]
	public async Task AnOpenReadDoesNotBlockAWrite()
	{
		ScheduleInfo schedule = NewSchedule();

		await InsertAsync( schedule );

		TaskCompletionSource readStarted = new( TaskCreationOptions.RunContinuationsAsynchronously );
		TaskCompletionSource writeDone = new( TaskCreationOptions.RunContinuationsAsynchronously );

		Task<( int before, int after )> read = Task.Run(
				() => _database.ApplicationData.ReadAsync(
						async ( session, context ) =>
						{
							int before = ( await session.GetScheduleAsync( context ) )!.State.Attempt;

							readStarted.SetResult();
							await writeDone.Task;

							return ( before, ( await session.GetScheduleAsync( context ) )!.State.Attempt );
						},
						schedule.Identifier ) );

		try
		{
			await readStarted.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );

			await Task.Run(
						() => _database.ApplicationData.WriteAsync(
								( session, context ) =>
									session.RecordOccurrenceCompletionAsync( context, 5, schedule.State.NextRun, LastRun ),
								schedule.Identifier ) )
					.WaitAsync( TimeSpan.FromSeconds( 5 ) );
		}
		finally
		{
			writeDone.TrySetResult();
		}

		( int before, int after ) = await read;

		Assert.Equal( 0, before );
		Assert.Equal( 0, after );
		Assert.Equal( 5, ( await ReadScheduleAsync( schedule.Identifier ) )!.State.Attempt );
	}
}

public sealed class SqliteScheduleStoreUnitOfWorkTests
	: ScheduleStoreUnitOfWorkTests<SqliteScheduleDatabaseFixture>, IClassFixture<SqliteScheduleDatabaseFixture>
{
	public SqliteScheduleStoreUnitOfWorkTests( SqliteScheduleDatabaseFixture database ) : base( database )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresScheduleStoreUnitOfWorkTests
	: ScheduleStoreUnitOfWorkTests<PostgresScheduleDatabaseFixture>, IClassFixture<PostgresScheduleDatabaseFixture>
{
	public PostgresScheduleStoreUnitOfWorkTests( PostgresScheduleDatabaseFixture database ) : base( database )
	{
	}
}
