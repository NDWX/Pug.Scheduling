using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Client;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Round-trips <see cref="IScheduler"/> through the API client against the live endpoint
/// pipeline. The point is substitutability: the client must behave like the in-process facade,
/// exceptions included, so callers above it cannot tell where the scheduler runs.
/// </summary>
public class SchedulerClientTests : IClassFixture<SchedulingApiFixture>
{
	private const string Owner = "client-owner-1";
	private const string OtherUser = "client-other-1";

	private readonly SchedulingApiFixture _fixture;
	private readonly IScheduler _scheduler;

	public SchedulerClientTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;
		_scheduler = new SchedulerClient( fixture.CreateApiClient( Owner ) );
	}

	private static ScheduleDefinition NewDefinition( string name = "Client schedule", string? domain = null,
													Recurrence? recurrence = null ) =>
		new()
		{
			Domain = domain ?? SchedulingApiFixture.Domain,
			Name = name,
			JobType = SchedulingApiFixture.JobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = recurrence ?? new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

	[Fact]
	public async Task RegisteredScheduleRoundTripsWithEveryModelSectionIntact()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( identifier, schedule.Identifier );
		Assert.Equal( SchedulingApiFixture.Domain, schedule.Definition.Domain );
		Assert.Equal( "Client schedule", schedule.Definition.Name );
		Assert.Equal( TimeSpan.FromHours( 1 ), schedule.Definition.Recurrence.Interval );
		Assert.Equal( "Etc/UTC", schedule.Definition.Recurrence.TimeZone );

		// Server-applied defaults survive serialization.
		Assert.Equal( MisfireHandlings.FireOnce, schedule.Definition.MisfirePolicy.Handling );
		Assert.Equal( 3, schedule.Definition.RetryPolicy.MaximumAttempts );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), schedule.Definition.Timeout );

		// Owner defaulted to the calling principal, and audit context came back composed.
		Assert.Equal( Owner, schedule.Owner.Identifier );
		Assert.Equal( "USER", schedule.Owner.Type );
		Assert.Equal( Owner, schedule.Registration.Actor.Identifier );

		Assert.True( schedule.State.Enabled );
		Assert.False( schedule.State.Paused );
		Assert.NotNull( schedule.State.NextRun );
		Assert.Equal( DateTimeKind.Utc, schedule.State.NextRun.Value.Kind );
		Assert.Null( schedule.ExecutionLease );
	}

	[Fact]
	public async Task RegistrationForAnotherOwnerCarriesTheOwnerAcross()
	{
		_fixture.Authorizations.Grant(
				Owner, SecurityOperations.Create, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		// Reading back a schedule owned by someone else is not covered by the owner rule either.
		_fixture.Authorizations.Grant(
				Owner, SecurityOperations.GetInfo, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		Reference owner = new() { Type = "USER", Identifier = "delegated-owner" };

		string identifier = await _scheduler.RegisterAsync( NewDefinition( "Delegated" ), owner );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( "delegated-owner", schedule.Owner.Identifier );
	}

	[Fact]
	public async Task ListingFiltersReachTheServer()
	{
		await _scheduler.RegisterAsync( NewDefinition( "Listed via client" ) );

		IEnumerable<ScheduleInfo> schedules = await _scheduler.GetSchedulesAsync(
				SchedulingApiFixture.Domain, new Reference { Type = "USER", Identifier = Owner } );

		Assert.All( schedules, schedule => Assert.Equal( Owner, schedule.Owner.Identifier ) );
		Assert.Contains( schedules, schedule => schedule.Definition.Name == "Listed via client" );

		// A job type nothing is registered under filters everything out.
		Assert.Empty(
				await _scheduler.GetSchedulesAsync(
						SchedulingApiFixture.Domain, new Reference { Type = "USER", Identifier = Owner },
						jobType: "unregistered" ) );
	}

	[Fact]
	public async Task LifecycleOperationsRoundTrip()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await _scheduler.UpdateDefinitionAsync(
				identifier,
				NewDefinition( "Renamed via client",
							recurrence: new Recurrence
							{
								Interval = TimeSpan.FromMinutes( 30 ), TimeZone = "Etc/UTC"
							} ) );

		ScheduleInfo updated = await _scheduler.GetScheduleAsync( identifier );

		Assert.Equal( "Renamed via client", updated.Definition.Name );
		Assert.Equal( TimeSpan.FromMinutes( 30 ), updated.Definition.Recurrence.Interval );
		Assert.Equal( Owner, updated.LastUpdate.Actor.Identifier );

		await _scheduler.PauseAsync( identifier );
		Assert.True( ( await _scheduler.GetScheduleAsync( identifier ) ).State.Paused );

		await _scheduler.ResumeAsync( identifier );
		Assert.False( ( await _scheduler.GetScheduleAsync( identifier ) ).State.Paused );

		await _scheduler.RunNowAsync( identifier );
		Assert.True( ( await _scheduler.GetScheduleAsync( identifier ) ).State.RunRequested );

		Assert.Empty( await _scheduler.GetRunsAsync( identifier ) );

		await _scheduler.DeleteAsync( identifier );

		await Assert.ThrowsAsync<UnknownScheduleException>( () => _scheduler.GetScheduleAsync( identifier ) );
	}

	[Fact]
	public async Task UnknownScheduleSurfacesTheAbstractionsException()
	{
		UnknownScheduleException exception =
			await Assert.ThrowsAsync<UnknownScheduleException>(
					() => _scheduler.GetScheduleAsync( "no-such-schedule" ) );

		Assert.Equal( "no-such-schedule", exception.Identifier );

		await Assert.ThrowsAsync<UnknownScheduleException>( () => _scheduler.RunNowAsync( "no-such-schedule" ) );
		await Assert.ThrowsAsync<UnknownScheduleException>( () => _scheduler.DeleteAsync( "no-such-schedule" ) );
	}

	[Fact]
	public async Task DeniedAccessSurfacesAsNotAuthorized()
	{
		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		IScheduler otherUser = new SchedulerClient( _fixture.CreateApiClient( OtherUser ) );

		await Assert.ThrowsAsync<NotAuthorized>( () => otherUser.GetScheduleAsync( identifier ) );
		await Assert.ThrowsAsync<NotAuthorized>( () => otherUser.DeleteAsync( identifier ) );
	}

	[Fact]
	public async Task ServerSideValidationSurfacesAsArgumentException()
	{
		ScheduleDefinition invalid = NewDefinition() with
		{
			Recurrence = new Recurrence { CronExpression = "not a cron expression", TimeZone = "Etc/UTC" }
		};

		ArgumentException exception =
			await Assert.ThrowsAsync<ArgumentException>( () => _scheduler.RegisterAsync( invalid ) );

		// The client reconstructs which argument the facade rejected, not just that something was.
		Assert.Equal( "recurrence", exception.ParamName );

		string identifier = await _scheduler.RegisterAsync( NewDefinition() );

		await Assert.ThrowsAsync<ArgumentException>(
				() => _scheduler.UpdateDefinitionAsync( identifier, NewDefinition( domain: "tenant-2" ) ) );
	}
}
