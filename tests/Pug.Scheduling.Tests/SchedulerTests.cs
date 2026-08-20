using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Leases;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

public class SchedulerTests : IClassFixture<ScheduleDatabaseFixture>, IClassFixture<LeaseDatabaseFixture>
{
	private const string Domain = "tenant-1";
	private const string OwnerUser = "owner-1";

	private readonly ScheduleDatabaseFixture _scheduleDatabase;
	private readonly IScheduler _scheduler;
	private readonly IExecutionLeaseProvider _leaseProvider;

	public SchedulerTests( ScheduleDatabaseFixture scheduleDatabase, LeaseDatabaseFixture leaseDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
		_leaseProvider = new DatabaseExecutionLeaseProvider( leaseDatabase.ApplicationData );
		_scheduler = CreateScheduler( SecurityManagerFactory.CreateAlwaysAuthorized( OwnerUser ) );
	}

	private IScheduler CreateScheduler( ISecurityManager securityManager ) =>
		new Scheduler(
				new SequentialIdGenerator( "schedule" ),
				_scheduleDatabase.ApplicationData,
				securityManager,
				_leaseProvider,
				new FakeJobTypeRegistry( "test" ),
				new SchedulerOptions() );

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
	public async Task RunNowBringsNextRunForward()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await _scheduler.RunNowAsync( identifier );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.True( Math.Abs( ( schedule.State.NextRun!.Value - DateTime.UtcNow ).TotalSeconds ) < 30 );
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
}
