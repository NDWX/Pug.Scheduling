using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Security;
using Pug.Application.Data.Extensions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Leases;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// Claim / misfire / retry / completion state machine of the execution coordinator, exercised
// against real SQLite schedule and lease stores.
public class ExecutionCoordinatorTests : IClassFixture<ScheduleDatabaseFixture>, IClassFixture<LeaseDatabaseFixture>
{
	private const string Domain = "tenant-1";
	private const string Claimant = "worker-1";

	// The database is shared by every test of the class, and a worker re-takes its own live
	// leases, so each test claims within a job type of its own: schedules another test left due
	// or leased never reach it.
	private readonly string _jobType = $"test-{Guid.NewGuid():N}";

	private readonly ScheduleDatabaseFixture _scheduleDatabase;
	private readonly IExecutionLeaseProvider _leaseProvider;
	private readonly ExecutionCoordinator _coordinator;

	public ExecutionCoordinatorTests( ScheduleDatabaseFixture scheduleDatabase, LeaseDatabaseFixture leaseDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
		_leaseProvider = new DatabaseExecutionLeaseProvider( leaseDatabase.ApplicationData );

		// The coordinator authorizes every operation against the claimant identity; these tests
		// cover the state machine, so the grant is unconditional here — CoordinatorAuthorization
		// covers denial.
		_coordinator = CreateCoordinator( "run", SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ) );
	}

	private ExecutionCoordinator CreateCoordinator( string runPrefix, ISecurityManager securityManager,
													ExecutionCoordinatorOptions? options = null ) =>
		new(
				new SequentialIdGenerator( runPrefix ),
				_scheduleDatabase.ApplicationData,
				_leaseProvider,
				securityManager,
				options ?? new ExecutionCoordinatorOptions(),
				NullLogger<ExecutionCoordinator>.Instance );

	private async Task<string> InsertScheduleAsync( DateTime? nextRun, int attempt = 0,
													string misfireHandling = MisfireHandlings.FireOnce,
													TimeSpan? misfireThreshold = null,
													int maximumAttempts = 3,
													TimeSpan? interval = null,
													string? jobType = null )
	{
		ScheduleInfo schedule = new()
		{
			Identifier = $"schedule-{Guid.NewGuid():N}",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			Definition = new ScheduleDefinition
			{
				Domain = Domain,
				Name = "Coordinator test schedule",
				JobType = jobType ?? _jobType,
				Payload = /*lang=json*/ """{"configuration":"report-1"}""",
				Recurrence = new Recurrence
				{
					Interval = interval ?? TimeSpan.FromHours( 1 ),
					TimeZone = "Etc/UTC"
				},
				MisfirePolicy = new MisfirePolicy
				{
					Handling = misfireHandling,
					Threshold = misfireThreshold ?? TimeSpan.FromMinutes( 5 )
				},
				RetryPolicy = new RetryPolicy
				{
					MaximumAttempts = maximumAttempts,
					Backoff = TimeSpan.FromMinutes( 1 )
				},
				Timeout = TimeSpan.FromMinutes( 10 )
			},
			State = new ScheduleState
			{
				Enabled = true,
				Paused = false,
				NextRun = nextRun,
				Attempt = attempt
			},
			Registration = new ActionContext<Reference>
			{
				Actor = new Reference { Type = "USER", Identifier = "owner-1" },
				Timestamp = DateTime.UtcNow
			}
		};

		await _scheduleDatabase.ApplicationData.PerformAsync(
				async ( session, context ) => await session.InsertAsync( context.schedule ),
				context: new { schedule } );

		return schedule.Identifier;
	}

	private Task<ScheduleInfo?> GetScheduleAsync( string identifier ) =>
		_scheduleDatabase.ApplicationData.ExecuteAsync(
				async ( session, context ) => await session.GetScheduleAsync( context.identifier ),
				context: new { identifier } );

	private Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.ExecuteAsync(
				async ( session, context ) => await session.GetRunsAsync( context.schedule ),
				context: new { schedule } );

	private Task ForceDueAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.PerformAsync(
				async ( session, context ) =>
					await session.UpdateNextRunAsync( context.schedule, context.nextRun ),
				context: new { schedule, nextRun = (DateTime?)DateTime.UtcNow.AddSeconds( -1 ) } );

	private async Task<ClaimedJob> ClaimSingleAsync( string schedule, int worker = 1 )
	{
		ClaimedJob? job = await _coordinator.ClaimDueJobAsync( Claimant, worker, _jobType );

		Assert.NotNull( job );
		Assert.Equal( schedule, job.Context.Schedule );

		return job;
	}

	[Fact]
	public async Task DueScheduleIsClaimedWithRunRecorded()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.Equal( Domain, job.Context.Domain );
		Assert.Equal( "owner-1", job.Context.Owner.Identifier );
		Assert.Equal( _jobType, job.Context.JobType );
		Assert.Equal( RunTriggers.Schedule, job.Context.Trigger );
		Assert.Equal( 0, job.Context.Attempt );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), job.Timeout );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( job.Context.Run, run.Identifier );
		Assert.Null( run.Finished );

		// The lease is held by the individual worker, not by its group.
		Assert.Equal( $"{Claimant}/1", ( await _leaseProvider.GetAsync( schedule ) ).ClaimedBy );
	}

	[Fact]
	public async Task NothingDueYieldsNoClaim()
	{
		await InsertScheduleAsync( DateTime.UtcNow.AddHours( 1 ) );

		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );
	}

	[Fact]
	public async Task ConcurrentCoordinatorsAdmitASingleClaim()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ExecutionCoordinator second =
			CreateCoordinator( "run2", SecurityManagerFactory.CreateAlwaysAuthorized( "worker-b" ) );

		ClaimedJob?[] claims = await Task.WhenAll(
				_coordinator.ClaimDueJobAsync( "worker-a", 1, _jobType ),
				second.ClaimDueJobAsync( "worker-b", 1, _jobType ) );

		Assert.Equal( 1, claims.Count( job => job?.Context.Schedule == schedule ) );
	}

	[Fact]
	public async Task WorkersOfOneClaimantNeverShareAClaim()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		// Same group identity, different worker numbers: the lease is per worker, so only one of
		// them may hold the schedule.
		ClaimedJob?[] claims = await Task.WhenAll(
				_coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ),
				_coordinator.ClaimDueJobAsync( Claimant, 2, _jobType ) );

		ClaimedJob job = Assert.Single( claims.OfType<ClaimedJob>() );

		Assert.Equal( schedule, job.Context.Schedule );
		Assert.Single( await GetRunsAsync( schedule ) );

		int winner = Array.IndexOf( claims, job ) + 1;

		Assert.Equal( $"{Claimant}/{winner}", ( await _leaseProvider.GetAsync( schedule ) ).ClaimedBy );

		// The loser keeps coming back empty while the winner holds the claim.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 3 - winner, _jobType ) );
	}

	[Fact]
	public async Task RestartedWorkerRetakesItsOwnLiveLeaseAndAdoptsTheRun()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob original = await ClaimSingleAsync( schedule, worker: 3 );

		// A restarted worker presents the same claimant and number while its predecessor's lease
		// is still live: it is the same holder, so it re-takes the lease and adopts the
		// unfinished run instead of recording a duplicate.
		ClaimedJob retaken = await ClaimSingleAsync( schedule, worker: 3 );

		Assert.Equal( original.Context.Run, retaken.Context.Run );
		Assert.Equal( original.Context.ScheduledFor, retaken.Context.ScheduledFor );
		Assert.Single( await GetRunsAsync( schedule ) );
		Assert.Equal( $"{Claimant}/3", ( await _leaseProvider.GetAsync( schedule ) ).ClaimedBy );

		// Another worker of the same group is still excluded.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 4, _jobType ) );
	}

	[Fact]
	public async Task ClaimSkipsACandidateLeasedByAPeerAndTakesTheNext()
	{
		string contested = await InsertScheduleAsync( DateTime.UtcNow.AddMinutes( -2 ) );
		string next = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		Assert.True( await _leaseProvider.TryAcquireAsync( contested, "peer/1", DateTime.UtcNow.AddMinutes( 2 ) ) );

		// A one-candidate window sees only the oldest due schedule, loses it, and comes back
		// empty although other work is due — which is what the window exists to prevent.
		ExecutionCoordinator narrow = CreateCoordinator(
				"run-narrow", SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ),
				new ExecutionCoordinatorOptions { ClaimCandidates = 1 } );

		Assert.Null( await narrow.ClaimDueJobAsync( Claimant, 1, _jobType ) );

		ClaimedJob job = await ClaimSingleAsync( next );

		Assert.Equal( next, job.Context.Schedule );
		Assert.Equal( "peer/1", ( await _leaseProvider.GetAsync( contested ) ).ClaimedBy );
		Assert.Empty( await GetRunsAsync( contested ) );
	}

	[Fact]
	public async Task JobTypeFilterClaimsOnlyThatType()
	{
		string otherType = $"{_jobType}-other";

		string ofThisType = await InsertScheduleAsync( DateTime.UtcNow.AddMinutes( -2 ) );
		string ofOtherType = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ), jobType: otherType );

		// The other type's schedule is due later, yet it is the one a claim for that type gets.
		ClaimedJob job = Assert.IsType<ClaimedJob>( await _coordinator.ClaimDueJobAsync( Claimant, 1, otherType ) );

		Assert.Equal( ofOtherType, job.Context.Schedule );
		Assert.Equal( otherType, job.Context.JobType );

		// Nothing else of that type is due, even though a schedule of another type is.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 2, otherType ) );

		Assert.Equal( ofThisType, ( await ClaimSingleAsync( ofThisType, worker: 2 ) ).Context.Schedule );
	}

	[Fact]
	public async Task ClaimWithoutJobTypeTakesAnyType()
	{
		string otherType = $"{_jobType}-other";

		// Older than anything other tests of the class leave due, so they head the window.
		string first = await InsertScheduleAsync( DateTime.UtcNow.AddDays( -2 ) );
		string second = await InsertScheduleAsync( DateTime.UtcNow.AddDays( -1 ), jobType: otherType );

		const string anyType = "any-type-worker";

		ClaimedJob? claimedFirst = await _coordinator.ClaimDueJobAsync( anyType, 1, null );
		ClaimedJob? claimedSecond = await _coordinator.ClaimDueJobAsync( anyType, 2, null );

		Assert.Equal( first, claimedFirst?.Context.Schedule );
		Assert.Equal( _jobType, claimedFirst?.Context.JobType );

		Assert.Equal( second, claimedSecond?.Context.Schedule );
		Assert.Equal( otherType, claimedSecond?.Context.JobType );
	}

	[Fact]
	public async Task CallerIdentityIsValidated()
	{
		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.ClaimDueJobAsync( Claimant, 0, _jobType ) );

		Assert.Equal( "worker", exception.ParamName );

		// The separator of the composed lease holder is reserved.
		exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.ClaimDueJobAsync( "group/1", 1, _jobType ) );

		Assert.Equal( "claimant", exception.ParamName );

		exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.RenewClaimAsync( "run-1", Claimant, -1 ) );

		Assert.Equal( "worker", exception.ParamName );

		exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.ReportCompletionAsync(
						"run-1", "a/b", 1, RunResults.Succeeded, new JobOutcome { Successful = true } ) );

		Assert.Equal( "claimant", exception.ParamName );
	}

	[Fact]
	public async Task SuccessfulCompletionAdvancesScheduleAndReleasesClaim()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		JobOutcome outcome = new() { Successful = true, Details = /*lang=json*/ """{"artifact":"blob-1"}""" };

		await _coordinator.ReportCompletionAsync( job.Context.Run, Claimant, 1, RunResults.Succeeded, outcome );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.NotNull( run.Finished );
		Assert.Equal( RunResults.Succeeded, run.Result );
		Assert.True( run.Outcome!.Successful );

		ScheduleInfo state = ( await GetScheduleAsync( schedule ) )!;

		Assert.Equal( 0, state.State.Attempt );
		Assert.Equal( RunResults.Succeeded, state.State.LastRun!.Result );

		Assert.True( Math.Abs( ( state.State.NextRun!.Value - DateTime.UtcNow.AddHours( 1 ) ).TotalSeconds ) < 30 );

		Assert.Null( await _leaseProvider.GetAsync( schedule ) );
	}

	[Fact]
	public async Task FailureSchedulesRetryPreservingOriginalOccurrence()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob first = await ClaimSingleAsync( schedule );

		DateTime originalOccurrence = first.Context.ScheduledFor;

		await _coordinator.ReportCompletionAsync(
				first.Context.Run, Claimant, 1, RunResults.Failed,
				new JobOutcome { Successful = false, Error = "boom" } );

		ScheduleInfo afterFailure = ( await GetScheduleAsync( schedule ) )!;

		Assert.Equal( 1, afterFailure.State.Attempt );

		// First retry backs off by the base backoff (1 minute).
		Assert.True( Math.Abs( ( afterFailure.State.NextRun!.Value - DateTime.UtcNow.AddMinutes( 1 ) ).TotalSeconds ) < 30 );

		await ForceDueAsync( schedule );

		ClaimedJob retry = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Retry, retry.Context.Trigger );
		Assert.Equal( 1, retry.Context.Attempt );
		Assert.Equal( originalOccurrence, retry.Context.ScheduledFor );

		Assert.Equal( 2, ( await GetRunsAsync( schedule ) ).Count() );
	}

	[Fact]
	public async Task ExhaustedRetriesRecordTerminalFailureAndAdvance()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ), maximumAttempts: 2 );

		ClaimedJob first = await ClaimSingleAsync( schedule );

		await _coordinator.ReportCompletionAsync(
				first.Context.Run, Claimant, 1, RunResults.Failed,
				new JobOutcome { Successful = false, Error = "boom" } );

		await ForceDueAsync( schedule );

		ClaimedJob retry = await ClaimSingleAsync( schedule );

		await _coordinator.ReportCompletionAsync(
				retry.Context.Run, Claimant, 1, RunResults.Failed,
				new JobOutcome { Successful = false, Error = "boom again" } );

		ScheduleInfo state = ( await GetScheduleAsync( schedule ) )!;

		// Terminal: attempt resets and the schedule advances to its next occurrence.
		Assert.Equal( 0, state.State.Attempt );
		Assert.Equal( RunResults.Failed, state.State.LastRun!.Result );

		Assert.True( Math.Abs( ( state.State.NextRun!.Value - DateTime.UtcNow.AddHours( 1 ) ).TotalSeconds ) < 30 );
	}

	[Fact]
	public async Task MisfireSkipRecordsSkippedRunAndAdvances()
	{
		string schedule = await InsertScheduleAsync(
				DateTime.UtcNow.AddMinutes( -10 ), misfireHandling: MisfireHandlings.Skip );

		// The only due occurrence was disposed of without execution.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( RunResults.Skipped, run.Result );
		Assert.Equal( RunTriggers.Misfire, run.Trigger );
		Assert.Null( run.Outcome );

		ScheduleInfo state = ( await GetScheduleAsync( schedule ) )!;

		Assert.True( Math.Abs( ( state.State.NextRun!.Value - DateTime.UtcNow.AddHours( 1 ) ).TotalSeconds ) < 30 );

		Assert.Null( await _leaseProvider.GetAsync( schedule ) );
	}

	[Fact]
	public async Task OverdueOccurrenceBeyondThresholdIsClassifiedAsMisfire()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddMinutes( -10 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		// FIRE_ONCE coalesces: the occurrence executes now, flagged as a misfire catch-up.
		Assert.Equal( RunTriggers.Misfire, job.Context.Trigger );
	}

	[Fact]
	public async Task RequestedRunIsClassifiedAsManualAndConsumesTheRequest()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddHours( 1 ) );

		await _scheduleDatabase.ApplicationData.PerformAsync(
				async ( session, context ) => await session.RequestRunAsync( context.schedule, context.now ),
				context: new { schedule, now = DateTime.UtcNow } );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Manual, job.Context.Trigger );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( RunTriggers.Manual, run.Trigger );

		// The request is consumed by the claim that honored it.
		Assert.False( ( await GetScheduleAsync( schedule ) )!.State.RunRequested );
	}

	[Fact]
	public async Task OrphanedRunOfLapsedClaimIsAdopted()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob original = await ClaimSingleAsync( schedule );

		// Simulate a crashed worker whose lease lapsed: the claim is gone but the run row
		// remains unfinished.
		await _leaseProvider.ReleaseAsync( schedule, $"{Claimant}/1" );

		ClaimedJob readopted = await ClaimSingleAsync( schedule );

		Assert.Equal( original.Context.Run, readopted.Context.Run );
		Assert.Single( await GetRunsAsync( schedule ) );
	}

	[Fact]
	public async Task ClaimRenewalTracksLeaseLifecycle()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );

		// The claim is the worker's, not its group's.
		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 2 ) );

		await _coordinator.ReportCompletionAsync(
				job.Context.Run, Claimant, 1, RunResults.Succeeded, new JobOutcome { Successful = true } );

		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );
	}

	[Fact]
	public async Task ReportingUnknownRunThrows()
	{
		await Assert.ThrowsAsync<UnknownRunException>(
				() => _coordinator.ReportCompletionAsync(
						"missing", Claimant, 1, RunResults.Succeeded, new JobOutcome { Successful = true } ) );
	}

	[Fact]
	public async Task UngrantedClaimantIsRefusedEveryOperation()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		// Authorization belongs to the coordinator, not to any hosting surface: an in-process
		// worker is held to the same grants as one calling in over a transport.
		ExecutionCoordinator denied = CreateCoordinator(
				"run-denied", SecurityManagerFactory.CreateAlwaysDenied( "unauthorized-worker" ) );

		await Assert.ThrowsAsync<NotAuthorized>( () => denied.ClaimDueJobAsync( "unauthorized-worker", 1, _jobType ) );

		// The refused claim must not have leased the schedule away from a legitimate worker.
		ClaimedJob job = await ClaimSingleAsync( schedule );

		await Assert.ThrowsAsync<NotAuthorized>(
				() => denied.RenewClaimAsync( job.Context.Run, "unauthorized-worker", 1 ) );

		await Assert.ThrowsAsync<NotAuthorized>(
				() => denied.ReportCompletionAsync(
						job.Context.Run, "unauthorized-worker", 1, RunResults.Succeeded,
						new JobOutcome { Successful = true } ) );
	}

	[Fact]
	public async Task InProcessCallerWithoutPrincipalIsNotChecked()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		// No principal identifies an in-process caller: endpoints reject unauthenticated requests
		// before dispatch, so a call arriving without one cannot have crossed a transport. An
		// in-process worker therefore needs no service identity, and holds no grants.
		ExecutionCoordinator inProcess =
			CreateCoordinator( "run-in-process", SecurityManagerFactory.CreateWithoutPrincipal() );

		ClaimedJob? job = await inProcess.ClaimDueJobAsync( "in-process-worker", 1, _jobType );

		Assert.Equal( schedule, job?.Context.Schedule );

		Assert.True( await inProcess.RenewClaimAsync( job!.Context.Run, "in-process-worker", 1 ) );

		await inProcess.ReportCompletionAsync(
				job.Context.Run, "in-process-worker", 1, RunResults.Succeeded,
				new JobOutcome { Successful = true } );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( RunResults.Succeeded, run.Result );
	}
}
