using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.DependencyInjection;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// The database clock over a real schedule store of each provider, run by the subclasses below. How
// the coordinator's claims follow it is in ExecutionCoordinatorTests (Database clock).
public abstract class SchedulingClockTests<TScheduleDatabase>
	where TScheduleDatabase : class, IScheduleDatabaseFixture
{
	// Two reads of the database's clock a moment apart, on different connections and through
	// different SQL; the SQLite direct read goes through a double, so it is not exact either.
	private static readonly TimeSpan Tolerance = TimeSpan.FromSeconds( 1 );

	private readonly TScheduleDatabase _scheduleDatabase;

	protected SchedulingClockTests( TScheduleDatabase scheduleDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
	}

	[Fact]
	public async Task DatabaseClockReadsTheDatabaseTime()
	{
		DateTime before = await _scheduleDatabase.SelectCurrentTimeAsync();
		DateTime now = await _scheduleDatabase.ReadDatabaseClockAsync();
		DateTime after = await _scheduleDatabase.SelectCurrentTimeAsync();

		Assert.Equal( DateTimeKind.Utc, now.Kind );
		Assert.InRange( now, before - Tolerance, after + Tolerance );
	}

	// The clock is read anew on every call, not fixed at the unit's start: a unit that waited on a
	// row lock must see the time after the wait.
	[Fact]
	public async Task DatabaseClockAdvancesWithinOneUnit()
	{
		( DateTime first, DateTime second ) = await _scheduleDatabase.ApplicationData.WriteAsync(
				async ( session, clock ) =>
				{
					DateTime first = await clock.GetUtcNowAsync( session );

					await Task.Delay( TimeSpan.FromMilliseconds( 50 ) );

					return ( first, await clock.GetUtcNowAsync( session ) );
				},
				new DatabaseClock() );

		Assert.InRange( second - first, TimeSpan.FromMilliseconds( 40 ), TimeSpan.FromSeconds( 10 ) );
	}

	// The scheduler's registration stamp and first occurrence come from the same clock read: the
	// database's, ignoring a host clock that runs hours ahead.
	[Fact]
	public async Task RegistrationIsStampedByTheDatabaseClock()
	{
		ServiceCollection services = new();
		services.AddLogging();
		services.AddSingleton( _scheduleDatabase.ApplicationData );
		services.AddSingleton( SecurityManagerFactory.CreateAlwaysAuthorized( "owner-1" ) );
		services.AddSingleton<TimeProvider>( new ManualTimeProvider( DateTime.UtcNow.AddHours( 6 ) ) );
		services.AddScheduling( new SequentialIdGenerator( $"clock-{Guid.NewGuid():N}" ) );
		services.AddSchedulableJobType( "test" );

		await using ServiceProvider provider = services.BuildServiceProvider();

		IScheduler scheduler = provider.GetRequiredService<IScheduler>();

		DateTime before = await _scheduleDatabase.ReadDatabaseClockAsync();
		string identifier = await scheduler.RegisterAsync(
				new ScheduleDefinition
				{
					Domain = "tenant-1",
					Name = "Clock test schedule",
					JobType = "test",
					Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
				} );
		DateTime after = await _scheduleDatabase.ReadDatabaseClockAsync();

		ScheduleInfo schedule = await scheduler.GetScheduleAsync( identifier );

		Assert.InRange( schedule.Registration.Timestamp, before, after );
		Assert.Equal( schedule.Registration.Timestamp + TimeSpan.FromHours( 1 ), schedule.State.NextRun );
	}
}

public sealed class SqliteSchedulingClockTests
	: SchedulingClockTests<SqliteScheduleDatabaseFixture>, IClassFixture<SqliteScheduleDatabaseFixture>
{
	public SqliteSchedulingClockTests( SqliteScheduleDatabaseFixture scheduleDatabase ) : base( scheduleDatabase )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresSchedulingClockTests
	: SchedulingClockTests<PostgresScheduleDatabaseFixture>, IClassFixture<PostgresScheduleDatabaseFixture>
{
	public PostgresSchedulingClockTests( PostgresScheduleDatabaseFixture scheduleDatabase ) : base( scheduleDatabase )
	{
	}
}

// Which clock AddScheduling gives the facades.
public sealed class SchedulingClockRegistrationTests
{
	[Fact]
	public void DefaultClockIsTheDatabaseClock()
	{
		ServiceCollection services = new();
		services.AddScheduling( new SequentialIdGenerator( "schedule" ) );

		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.IsType<DatabaseClock>( provider.GetRequiredService<ISchedulingClock>() );
	}

	// A registered TimeProvider is not taken as the facades' clock: only an ISchedulingClock is.
	[Fact]
	public void RegisteredTimeProviderDoesNotReplaceTheDatabaseClock()
	{
		ServiceCollection services = new();
		services.AddSingleton<TimeProvider>( new ManualTimeProvider( DateTime.UtcNow ) );
		services.AddScheduling( new SequentialIdGenerator( "schedule" ) );

		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.IsType<DatabaseClock>( provider.GetRequiredService<ISchedulingClock>() );
	}

	[Theory]
	[InlineData( true )]
	[InlineData( false )]
	public void HostRegisteredClockReplacesTheDefault( bool registeredFirst )
	{
		ISchedulingClock hostClock = new TimeProviderClock( new ManualTimeProvider( DateTime.UtcNow ) );

		ServiceCollection services = new();

		if( registeredFirst )
			services.AddSingleton( hostClock );

		services.AddScheduling( new SequentialIdGenerator( "schedule" ) );

		if( !registeredFirst )
			services.AddSingleton( hostClock );

		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Same( hostClock, provider.GetRequiredService<ISchedulingClock>() );
	}
}
