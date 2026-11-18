using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// The scheduler facade over a real schedule store, run against each provider by the subclasses
// below, which declare the class fixture.
public abstract class SchedulerTests<TScheduleDatabase>
	where TScheduleDatabase : class, IScheduleDatabaseFixture
{
	private const string Domain = "tenant-1";
	private const string OwnerUser = "owner-1";

	private readonly TScheduleDatabase _scheduleDatabase;
	private readonly IScheduler _scheduler;

	protected SchedulerTests( TScheduleDatabase scheduleDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
		_scheduler = CreateScheduler( SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ) );
	}

	private IScheduler CreateScheduler( ISecurityManager securityManager, TimeProvider? timeProvider = null,
										SchedulerOptions? options = null ) =>
		new Scheduler(
				new SequentialIdGenerator( "schedule" ),
				_scheduleDatabase.ApplicationData,
				securityManager,
				new FakeJobTypeRegistry( "test" ),
				options ?? new SchedulerOptions(),
				new TimeProviderClock( timeProvider ?? TimeProvider.System ) );

	private static ScheduleDefinition NewDefinition( string jobType = "test", string name = "Test schedule",
													Recurrence? recurrence = null ) =>
		new()
		{
			Domain = Domain,
			Name = name,
			JobType = jobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = recurrence ?? new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

	private static Reference User( string identifier ) => new() { Type = "USER", Identifier = identifier };

	// Every time the facade stamps comes from the injected clock: the next occurrence and the
	// registration and update action contexts.
	[Fact]
	public async Task TimesComeFromTheInjectedClock()
	{
		DateTime registeredAt = new( 2031, 3, 4, 5, 6, 7, DateTimeKind.Utc );
		ManualTimeProvider clock = new( registeredAt );
		IScheduler scheduler = CreateScheduler( SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ), clock );

		string identifier = await scheduler.RegisterAsync( NewDefinition() );

		ScheduleInfo registered = await scheduler.GetScheduleAsync( identifier );

		Assert.Equal( registeredAt.AddHours( 1 ), registered.State.NextRun );
		Assert.Equal( registeredAt, registered.Registration.Timestamp );

		clock.Advance( TimeSpan.FromDays( 1 ) );

		await scheduler.UpdateDefinitionAsync(
				identifier,
				NewDefinition( recurrence: new Recurrence { Interval = TimeSpan.FromHours( 2 ), TimeZone = "Etc/UTC" } ) );

		ScheduleInfo updated = await scheduler.GetScheduleAsync( identifier );

		Assert.Equal( registeredAt.AddDays( 1 ).AddHours( 2 ), updated.State.NextRun );
		Assert.Equal( registeredAt.AddDays( 1 ), updated.LastUpdate!.Timestamp );
	}

	[Fact]
	public async Task RegistrationPersistsDefinitionAppliesDefaultsAndComputesNextRun()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( Domain, schedule.Definition.Domain );

		// Owner defaults to the registering principal.
		Assert.Equal( OwnerUser, schedule.Owner.Identifier );
		Assert.Equal( "USER", schedule.Owner.Type );

		Assert.Equal( "test", schedule.Definition.JobType );
		Assert.Equal( TimeSpan.FromHours( 1 ), schedule.Definition.Recurrence.Interval );

		// Service-level defaults applied to omitted policies.
		Assert.Equal( MisfireHandlings.FireOnce, schedule.Definition.MisfirePolicy.Handling );
		Assert.Equal( 3, schedule.Definition.RetryPolicy.MaximumAttempts );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), schedule.Definition.Timeout );

		Assert.True( schedule.State.Enabled );
		Assert.False( schedule.State.Paused );
		Assert.Equal( 0, schedule.State.Attempt );
		Assert.NotNull( schedule.State.NextRun );

		Assert.True( Math.Abs( ( schedule.State.NextRun.Value - DateTime.UtcNow.AddHours( 1 ) ).TotalSeconds ) < 30 );

		Assert.Equal( OwnerUser, schedule.Registration.Actor.Identifier );
		Assert.Null( schedule.LastUpdate );
		Assert.Null( schedule.ExecutionLease );
	}

	[Fact]
	public async Task ListingFiltersByOwnerAndJobType()
	{
		string mine = await _scheduler.RegisterAsync( NewDefinition( name: "Mine" ) );
		string theirs = await _scheduler.RegisterAsync( NewDefinition( name: "Theirs" ), User( "other-user" ) );

		IEnumerable<ScheduleInfo> owned = await _scheduler.GetSchedulesAsync( Domain, owner: User( OwnerUser ) );

		Assert.Contains( owned, schedule => schedule.Identifier == mine );
		Assert.DoesNotContain( owned, schedule => schedule.Identifier == theirs );

		IEnumerable<ScheduleInfo> byJobType = await _scheduler.GetSchedulesAsync( Domain, jobType: "test" );

		Assert.Contains( byJobType, schedule => schedule.Identifier == mine );
	}

	[Fact]
	public async Task UpdateRecomputesNextRunWhenRecurrenceChangesAndStampsLastUpdate()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		ScheduleDefinition updated = NewDefinition(
				recurrence: new Recurrence { Interval = TimeSpan.FromMinutes( 5 ), TimeZone = "Etc/UTC" } );

		await _scheduler.UpdateDefinitionAsync( identifier, updated );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( TimeSpan.FromMinutes( 5 ), schedule.Definition.Recurrence.Interval );
		Assert.NotNull( schedule.LastUpdate );

		Assert.True( Math.Abs( ( schedule.State.NextRun!.Value - DateTime.UtcNow.AddMinutes( 5 ) ).TotalSeconds ) < 30 );
	}

	[Fact]
	public async Task DomainIsImmutable()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		ScheduleDefinition foreignDomain = NewDefinition() with { Domain = "tenant-2" };

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.UpdateDefinitionAsync( identifier, foreignDomain ) );
	}

	[Fact]
	public async Task PauseAndResumeToggleState()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await _scheduler.PauseAsync( identifier );

		Assert.True( ( await _scheduler.GetScheduleAsync( identifier ) ).State.Paused );

		await _scheduler.ResumeAsync( identifier );

		Assert.False( ( await _scheduler.GetScheduleAsync( identifier ) ).State.Paused );
	}

	[Fact]
	public async Task RunNowRaisesARequestWithoutMovingTheNextRun()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		DateTime? nextRun = ( await _scheduler.GetScheduleAsync( identifier ) ).State.NextRun;

		await _scheduler.RunNowAsync( identifier );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( nextRun, schedule.State.NextRun );
		Assert.True( schedule.State.RunRequested );
	}

	[Fact]
	public async Task DeleteRemovesSchedule()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await _scheduler.DeleteAsync( identifier );

		await Assert.ThrowsAsync<UnknownScheduleException>( () => _scheduler.GetScheduleAsync( identifier ) );
	}

	[Fact]
	public async Task OwnerMayOperateWithoutAclGrantButOthersMayNot()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		// Every ACL check denies: only the owner rule can authorize.
		IScheduler ownerWithoutGrants = CreateScheduler( SecurityManagerFactory.CreateAlwaysDenied( OwnerUser ) );

		ScheduleInfo schedule = await ownerWithoutGrants.GetScheduleAsync( identifier );

		Assert.Equal( identifier, schedule.Identifier );

		await ownerWithoutGrants.PauseAsync( identifier );
		await ownerWithoutGrants.ResumeAsync( identifier );

		// The owner rule extends to creation: registering one's own schedule needs no grant,
		// registering on behalf of another principal does.
		string selfRegistered = await ownerWithoutGrants.RegisterAsync( NewDefinition( name: "Self-registered" ) );

		Assert.Equal( OwnerUser, ( await ownerWithoutGrants.GetScheduleAsync( selfRegistered ) ).Owner.Identifier );

		await Assert.ThrowsAsync<NotAuthorized>(
				() => ownerWithoutGrants.RegisterAsync( NewDefinition( name: "For someone else" ), User( "other-user" ) ) );

		IScheduler intruder = CreateScheduler( SecurityManagerFactory.CreateAlwaysDenied( "intruder" ) );

		await Assert.ThrowsAsync<NotAuthorized>( () => intruder.GetScheduleAsync( identifier ) );
		await Assert.ThrowsAsync<NotAuthorized>( () => intruder.DeleteAsync( identifier ) );
	}

	private static Reference Service( string identifier ) => new() { Type = "SERVICE", Identifier = identifier };

	// A USER and a SERVICE principal share an identifier: the listing exemption and the store
	// filter must tell them apart by type. Returns the caller (the shared-identifier user, holding
	// only the grants given) and the two schedules.
	private async Task<(IScheduler Caller, string UserSchedule, string ServiceSchedule)> RegisterSharedIdentifierAsync(
		bool grantList )
	{
		TestAuthorizations authorizations = new();
		authorizations.Grant( OwnerUser, SecurityOperations.CreateOnBehalf, "SERVICE", Domain, OwnerUser );

		if( grantList )
			authorizations.Grant( OwnerUser, SecurityOperations.List, SecurityObjectTypes.Schedule, Domain );

		IScheduler caller = CreateGrantedScheduler( authorizations );

		string userSchedule = await caller.RegisterAsync( NewDefinition( name: "User's" ) );
		string serviceSchedule = await caller.RegisterAsync( NewDefinition( name: "Service's" ), Service( OwnerUser ) );

		return ( caller, userSchedule, serviceSchedule );
	}

	[Fact]
	public async Task ListingOwnUserSchedulesNeedsNoGrantAndExcludesAnotherTypeWithTheSameIdentifier()
	{
		( IScheduler caller, string userSchedule, string serviceSchedule ) =
			await RegisterSharedIdentifierAsync( grantList: false );

		IEnumerable<ScheduleInfo> listed = await caller.GetSchedulesAsync( Domain, owner: User( OwnerUser ) );

		Assert.Contains( listed, schedule => schedule.Identifier == userSchedule );
		Assert.DoesNotContain( listed, schedule => schedule.Identifier == serviceSchedule );
	}

	[Fact]
	public async Task ListingAnotherTypeWithTheSameIdentifierNeedsTheListGrant()
	{
		( IScheduler caller, _, _ ) = await RegisterSharedIdentifierAsync( grantList: false );

		await Assert.ThrowsAsync<NotAuthorized>( () => caller.GetSchedulesAsync( Domain, owner: Service( OwnerUser ) ) );
	}

	[Fact]
	public async Task ListingByOwnerWithTheListGrantMatchesTypeAndIdentifier()
	{
		( IScheduler caller, string userSchedule, string serviceSchedule ) =
			await RegisterSharedIdentifierAsync( grantList: true );

		IEnumerable<ScheduleInfo> listed = await caller.GetSchedulesAsync( Domain, owner: Service( OwnerUser ) );

		Assert.Contains( listed, schedule => schedule.Identifier == serviceSchedule );
		Assert.DoesNotContain( listed, schedule => schedule.Identifier == userSchedule );
	}

	[Theory]
	[InlineData( null, "id" )]
	[InlineData( "", "id" )]
	[InlineData( " ", "id" )]
	[InlineData( "USER", null )]
	[InlineData( "USER", "" )]
	[InlineData( "USER", " " )]
	public async Task ListingWithAnIncompleteOwnerIsRejected( string? type, string? identifier ) =>
		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.GetSchedulesAsync( Domain, owner: new Reference { Type = type!, Identifier = identifier! } ) );

	private IScheduler CreateGrantedScheduler( TestAuthorizations authorizations ) =>
		CreateScheduler(
				SecurityManagerFactory.CreateContextual(
						() => OwnerUser,
						( user, operation, resource ) => authorizations.IsGranted( user, operation, resource ) ) );

	[Fact]
	public async Task RegisteringOnBehalfWithGrantForThatOwnerCreatesTheSchedule()
	{
		TestAuthorizations authorizations = new();
		authorizations.Grant( OwnerUser, SecurityOperations.CreateOnBehalf, "USER", Domain, "other-user" );

		string identifier = await CreateGrantedScheduler( authorizations )
			.RegisterAsync( NewDefinition(), User( "other-user" ) );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( "USER", schedule.Owner.Type );
		Assert.Equal( "other-user", schedule.Owner.Identifier );
	}

	[Fact]
	public async Task RegisteringOnBehalfWithoutGrantIsDenied()
	{
		IScheduler scheduler = CreateGrantedScheduler( new TestAuthorizations() );

		await Assert.ThrowsAsync<NotAuthorized>( () => scheduler.RegisterAsync( NewDefinition(), User( "other-user" ) ) );
	}

	[Fact]
	public async Task OnBehalfGrantForAnotherIdentifierTypeOrDomainIsDenied()
	{
		TestAuthorizations authorizations = new();
		authorizations.Grant( OwnerUser, SecurityOperations.CreateOnBehalf, "USER", Domain, "someone-else" );
		authorizations.Grant( OwnerUser, SecurityOperations.CreateOnBehalf, "GROUP", Domain, "other-user" );
		authorizations.Grant( OwnerUser, SecurityOperations.CreateOnBehalf, "USER", "tenant-2", "other-user" );

		IScheduler scheduler = CreateGrantedScheduler( authorizations );

		await Assert.ThrowsAsync<NotAuthorized>( () => scheduler.RegisterAsync( NewDefinition(), User( "other-user" ) ) );
	}

	[Fact]
	public async Task SelfRegistrationNeedsNoOnBehalfGrant()
	{
		string identifier = await CreateGrantedScheduler( new TestAuthorizations() ).RegisterAsync( NewDefinition() );

		Assert.Equal( OwnerUser, ( await _scheduler.GetScheduleAsync( identifier ) ).Owner.Identifier );
	}

	[Fact]
	public async Task InvalidRegistrationsAreRejected()
	{
		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( NewDefinition( jobType: "unregistered" ) ) );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync(
						NewDefinition( recurrence: new Recurrence { TimeZone = "Etc/UTC" } ) ) );

		await Assert.ThrowsAsync<UnknownScheduleException>( () => _scheduler.GetScheduleAsync( "missing" ) );
	}

	private static readonly TimeSpan WholeSecondsBound = TimeSpan.FromSeconds( Math.Floor( TimerBounds.Maximum.TotalSeconds ) );

	[Fact]
	public async Task TimeoutBeyondTheTimerBoundIsRejected()
	{
		// The bound (2^32-2 ms) is not itself whole seconds, so the whole-second limits straddle it.
		ScheduleDefinition definition = NewDefinition() with { Timeout = WholeSecondsBound + TimeSpan.FromSeconds( 1 ) };

		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( definition ) );

		Assert.Equal( "definition", exception.ParamName );

		// The largest whole-second timeout within the bound is accepted.
		await _scheduler.RegisterAsync( NewDefinition() with { Timeout = WholeSecondsBound } );
	}

	[Fact]
	public void DefaultTimeoutBeyondTheTimerBoundIsRejected()
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(
				() => new Scheduler(
						new SequentialIdGenerator( "schedule" ), _scheduleDatabase.ApplicationData,
						SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ), new FakeJobTypeRegistry( "test" ),
						new SchedulerOptions { DefaultTimeout = WholeSecondsBound + TimeSpan.FromSeconds( 1 ) },
						new TimeProviderClock( TimeProvider.System ) ) );

		Assert.Equal( "options", exception.ParamName );
	}

	// == Durations: whole seconds, minimums ==

	private static readonly TimeSpan SubSecond = TimeSpan.FromMilliseconds( 1500 );

	public static TheoryData<string> DurationFields => new() { "Interval", "Threshold", "Backoff", "Timeout" };

	// A definition whose one named duration is set to the value; the rest are valid.
	private static ScheduleDefinition WithDuration( string field, TimeSpan value ) =>
		field switch
		{
			"Interval" => NewDefinition( recurrence: new Recurrence { Interval = value, TimeZone = "Etc/UTC" } ),
			"Threshold" => NewDefinition() with
			{
				MisfirePolicy = new MisfirePolicy { Handling = MisfireHandlings.Skip, Threshold = value }
			},
			"Backoff" => NewDefinition() with { RetryPolicy = new RetryPolicy { MaximumAttempts = 2, Backoff = value } },
			_ => NewDefinition() with { Timeout = value }
		};

	[Theory]
	[MemberData( nameof(DurationFields) )]
	public async Task SubSecondDurationIsRejectedAtRegistration( string field )
	{
		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( WithDuration( field, SubSecond ) ) );

		Assert.Contains( exception.ParamName, new[] { "definition", "recurrence" } );
		Assert.Contains( "whole number of seconds", exception.Message );

		// The same value in whole seconds is accepted.
		await _scheduler.RegisterAsync( WithDuration( field, TimeSpan.FromSeconds( 30 ) ) );
	}

	[Theory]
	[MemberData( nameof(DurationFields) )]
	public async Task SubSecondDurationIsRejectedAtUpdate( string field )
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.UpdateDefinitionAsync( identifier, WithDuration( field, SubSecond ) ) );
	}

	[Fact]
	public async Task AWindowsTimeZoneIsRejectedAtRegistrationAndUpdate()
	{
		Recurrence windows = new() { CronExpression = "0 7 * * MON", TimeZone = "AUS Eastern Standard Time" };

		await Assert.ThrowsAsync<ArgumentException>( () => _scheduler.RegisterAsync( NewDefinition( recurrence: windows ) ) );

		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.UpdateDefinitionAsync( identifier, NewDefinition( recurrence: windows ) ) );
	}

	[Fact]
	public async Task ZeroIntervalIsRejected()
	{
		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( WithDuration( "Interval", TimeSpan.Zero ) ) );

		// A sub-second positive interval is the whole-seconds rule, and one second is the least.
		await _scheduler.RegisterAsync( WithDuration( "Interval", TimeSpan.FromSeconds( 1 ) ) );
	}

	[Fact]
	public async Task ZeroTimeoutIsTheDefaultAndNeverStoredAsZero()
	{
		// Zero is how a registration omits the timeout; it is replaced by the (at least 5 s) default.
		string identifier = await _scheduler.RegisterAsync( NewDefinition() with { Timeout = TimeSpan.Zero } );

		Assert.Equal( TimeSpan.FromMinutes( 10 ), ( await _scheduler.GetScheduleAsync( identifier ) ).Definition.Timeout );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( NewDefinition() with { Timeout = TimeSpan.FromSeconds( -1 ) } ) );
	}

	[Fact]
	public async Task SpecifiedTimeoutBelowFiveSecondsIsRejectedAndFiveSecondsIsAccepted()
	{
		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( NewDefinition() with { Timeout = TimeSpan.FromSeconds( 4 ) } ) );

		Assert.Equal( "definition", exception.ParamName );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( NewDefinition() with { Timeout = TimeSpan.FromSeconds( 1 ) } ) );

		string identifier = await _scheduler.RegisterAsync( NewDefinition() with { Timeout = TimeSpan.FromSeconds( 5 ) } );

		Assert.Equal( TimeSpan.FromSeconds( 5 ), ( await _scheduler.GetScheduleAsync( identifier ) ).Definition.Timeout );

		// The update path applies the same floor.
		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.UpdateDefinitionAsync( identifier, NewDefinition() with { Timeout = TimeSpan.FromSeconds( 4 ) } ) );
	}

	[Fact]
	public async Task ThresholdBelowTheMinimumIsRejectedAndTheMinimumIsAccepted()
	{
		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.FromSeconds( 14 ) ) ) );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.Zero ) ) );

		await _scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.FromSeconds( 15 ) ) );
	}

	[Fact]
	public async Task ACustomMinimumMisfireThresholdIsHonoured()
	{
		IScheduler scheduler = CreateScheduler(
				SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ),
				options: new SchedulerOptions
				{
					MinimumMisfireThreshold = TimeSpan.FromSeconds( 2 ),
					DefaultMisfirePolicy = new MisfirePolicy
					{
						Handling = MisfireHandlings.FireOnce, Threshold = TimeSpan.FromSeconds( 2 )
					}
				} );

		await scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.FromSeconds( 2 ) ) );

		await Assert.ThrowsAsync<ArgumentException>(
				() => scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.FromSeconds( 1 ) ) ) );

		// Larger than the default minimum is of course fine too, and the default minimum no longer applies.
		await scheduler.RegisterAsync( WithDuration( "Threshold", TimeSpan.FromSeconds( 20 ) ) );
	}

	public static TheoryData<SchedulerOptions> InvalidOptions => new()
	{
		new SchedulerOptions { MinimumMisfireThreshold = SubSecond },
		new SchedulerOptions { MinimumMisfireThreshold = TimeSpan.FromSeconds( -1 ) },
		new SchedulerOptions
		{
			DefaultMisfirePolicy = new MisfirePolicy { Handling = MisfireHandlings.Skip, Threshold = TimeSpan.FromSeconds( 14 ) }
		},
		new SchedulerOptions
		{
			DefaultMisfirePolicy = new MisfirePolicy { Handling = MisfireHandlings.Skip, Threshold = SubSecond + TimeSpan.FromMinutes( 1 ) }
		},
		new SchedulerOptions { DefaultRetryPolicy = new RetryPolicy { MaximumAttempts = 3, Backoff = SubSecond } },
		new SchedulerOptions { DefaultTimeout = SubSecond + TimeSpan.FromMinutes( 1 ) },
		new SchedulerOptions { DefaultTimeout = TimeSpan.Zero },
		new SchedulerOptions { DefaultTimeout = TimeSpan.FromSeconds( 4 ) }
	};

	[Theory]
	[MemberData( nameof(InvalidOptions) )]
	public void InvalidOptionsAreRejectedAtConstruction( SchedulerOptions options )
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(
				() => CreateScheduler( SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ), options: options ) );

		Assert.Equal( "options", exception.ParamName );
	}
}

public sealed class SqliteSchedulerTests : SchedulerTests<SqliteScheduleDatabaseFixture>,
		IClassFixture<SqliteScheduleDatabaseFixture>
{
	public SqliteSchedulerTests( SqliteScheduleDatabaseFixture scheduleDatabase )
		: base( scheduleDatabase )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresSchedulerTests : SchedulerTests<PostgresScheduleDatabaseFixture>,
		IClassFixture<PostgresScheduleDatabaseFixture>
{
	public PostgresSchedulerTests( PostgresScheduleDatabaseFixture scheduleDatabase )
		: base( scheduleDatabase )
	{
	}
}
