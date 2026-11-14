using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// Claim / adopt / misfire / retry / completion state machine of the execution coordinator, and the
// management writes that race it, exercised against a real schedule store of each provider by the
// subclasses below (which declare the class fixture).
//
// Time is the test's own clock, shared by every coordinator and scheduler a test creates: a claim
// lapses when the clock passes its lease, exactly as it would in production.
public abstract class ExecutionCoordinatorTests<TScheduleDatabase>
	where TScheduleDatabase : class, IScheduleDatabaseFixture
{
	private const string Domain = "tenant-1";
	private const string Owner = "owner-1";
	private const string Claimant = "worker-1";

	private static readonly TimeSpan LeaseDuration = new ExecutionCoordinatorOptions().LeaseDuration;

	// The database is shared by every test of the class, and a worker re-takes its own claims, so
	// each test claims within a job type of its own: schedules another test left due or claimed
	// never reach it.
	private readonly string _jobType = $"test-{Guid.NewGuid():N}";

	private readonly TScheduleDatabase _scheduleDatabase;
	// Whole milliseconds: Postgres keeps microseconds, so stored times then compare exactly.
	private readonly ManualTimeProvider _clock = new( DateTime.UnixEpoch.AddMilliseconds(
			Math.Floor( ( DateTime.UtcNow - DateTime.UnixEpoch ).TotalMilliseconds ) ) );
	private readonly ExecutionCoordinator _coordinator;
	private readonly Scheduler _scheduler;

	protected ExecutionCoordinatorTests( TScheduleDatabase scheduleDatabase )
	{
		_scheduleDatabase = scheduleDatabase;

		// The coordinator authorizes every operation against the claimant identity; these tests
		// cover the state machine, so the grant is unconditional here — CoordinatorAuthorization
		// covers denial.
		_coordinator = CreateCoordinator( "run", SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ) );

		// Owns the schedules InsertScheduleAsync creates, so the owner rule admits every operation.
		_scheduler = new Scheduler(
				new SequentialIdGenerator( "schedule-management" ),
				_scheduleDatabase.ApplicationData,
				SecurityManagerFactory.CreateAlwaysAuthorized( Owner ),
				new FakeJobTypeRegistry( _jobType ),
				new SchedulerOptions(),
				_clock );
	}

	private DateTime Now => _clock.GetUtcNow().UtcDateTime;

	private ExecutionCoordinator CreateCoordinator( string runPrefix, ISecurityManager securityManager,
													ExecutionCoordinatorOptions? options = null,
													TimeProvider? timeProvider = null ) =>
		new(
				new SequentialIdGenerator( runPrefix ),
				_scheduleDatabase.ApplicationData,
				securityManager,
				options ?? new ExecutionCoordinatorOptions(),
				timeProvider ?? _clock,
				NullLogger<ExecutionCoordinator>.Instance );

	private static ScheduleDefinition NewDefinition( string jobType, string misfireHandling = MisfireHandlings.FireOnce,
													TimeSpan? misfireThreshold = null, int maximumAttempts = 3,
													TimeSpan? interval = null ) =>
		new()
		{
			Domain = Domain,
			Name = "Coordinator test schedule",
			JobType = jobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = new Recurrence { Interval = interval ?? TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" },
			MisfirePolicy = new MisfirePolicy
			{
				Handling = misfireHandling,
				Threshold = misfireThreshold ?? TimeSpan.FromMinutes( 5 )
			},
			RetryPolicy = new RetryPolicy { MaximumAttempts = maximumAttempts, Backoff = TimeSpan.FromMinutes( 1 ) },
			Timeout = TimeSpan.FromMinutes( 10 )
		};

	private async Task<string> InsertScheduleAsync( DateTime? nextRun, string misfireHandling = MisfireHandlings.FireOnce,
													TimeSpan? misfireThreshold = null, int maximumAttempts = 3,
													string? jobType = null )
	{
		ScheduleInfo schedule = new()
		{
			Identifier = $"schedule-{Guid.NewGuid():N}",
			Owner = new Reference { Type = "USER", Identifier = Owner },
			Definition = NewDefinition( jobType ?? _jobType, misfireHandling, misfireThreshold, maximumAttempts ),
			State = new ScheduleState { Enabled = true, Paused = false, NextRun = nextRun, Attempt = 0 },
			Registration = new ActionContext<Reference>
			{
				Actor = new Reference { Type = "USER", Identifier = Owner },
				Timestamp = Now
			}
		};

		await _scheduleDatabase.ApplicationData.WriteAsync( ( session, context ) => session.InsertAsync( context ), schedule );

		return schedule.Identifier;
	}

	private Task<string> InsertDueScheduleAsync( string misfireHandling = MisfireHandlings.FireOnce,
												int maximumAttempts = 3 ) =>
		InsertScheduleAsync( Now.AddSeconds( -1 ), misfireHandling, maximumAttempts: maximumAttempts );

	private async Task<ScheduleInfo> GetScheduleAsync( string identifier ) =>
		( await _scheduleDatabase.ApplicationData.ReadAsync(
				( session, context ) => session.GetScheduleAsync( context ), identifier ) )!;

	private async Task<ScheduleRun[]> GetRunsAsync( string schedule ) =>
		( await _scheduleDatabase.ApplicationData.ReadAsync(
				( session, context ) => session.GetRunsAsync( context ), schedule ) ).ToArray();

	// Settles a race iteration: the next one then claims only its own schedule.
	private Task DeleteScheduleAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.WriteAsync( ( session, context ) => session.DeleteAsync( context ), schedule );

	// Every claim made so far lapses: its worker is taken to have crashed.
	private void LapseClaims() => _clock.Advance( LeaseDuration + TimeSpan.FromSeconds( 1 ) );

	private async Task<ClaimedJob> ClaimSingleAsync( string schedule, int worker = 1, ExecutionCoordinator? coordinator = null,
													string claimant = Claimant )
	{
		ClaimedJob? job = await ( coordinator ?? _coordinator ).ClaimDueJobAsync( claimant, worker, _jobType );

		Assert.NotNull( job );
		Assert.Equal( schedule, job.Context.Schedule );

		return job;
	}

	private Task CompleteAsync( ClaimedJob job, int worker = 1, string result = RunResults.Succeeded ) =>
		_coordinator.ReportCompletionAsync(
				job.Context.Run, Claimant, worker, result,
				new JobOutcome { Successful = result == RunResults.Succeeded } );

	// == Claim ==

	[Fact]
	public async Task DueScheduleIsClaimedWithRunRecorded()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.Equal( Domain, job.Context.Domain );
		Assert.Equal( Owner, job.Context.Owner.Identifier );
		Assert.Equal( _jobType, job.Context.JobType );
		Assert.Equal( RunTriggers.Schedule, job.Context.Trigger );
		Assert.Equal( 0, job.Context.Attempt );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), job.Timeout );
		Assert.Equal( Now + LeaseDuration, job.ClaimExpiry );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( job.Context.Run, run.Identifier );
		Assert.Null( run.Finished );

		// The lease is on the schedule row, held by the individual worker, not by its group.
		ExecutionLease lease = ( await GetScheduleAsync( schedule ) ).ExecutionLease;

		Assert.Equal( job.Context.Run, lease.Run );
		Assert.Equal( $"{Claimant}/1", lease.ClaimedBy );
		Assert.Equal( job.ClaimExpiry, lease.Expiry );
	}

	// Due-ness, the run's start and the claim's expiry follow the injected clock: a schedule due
	// tomorrow by the system clock is claimed when the coordinator's clock says it is tomorrow.
	[Fact]
	public async Task ClaimTakesItsTimeFromTheInjectedClock()
	{
		DateTime due = Now.AddDays( 1 );
		ManualTimeProvider tomorrow = new( due.AddSeconds( 1 ) );
		ExecutionCoordinator coordinator = CreateCoordinator(
				"run-clock", SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ), timeProvider: tomorrow );

		string schedule = await InsertScheduleAsync( due );

		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );

		ClaimedJob job = await ClaimSingleAsync( schedule, coordinator: coordinator );

		Assert.Equal( tomorrow.GetUtcNow().UtcDateTime + LeaseDuration, job.ClaimExpiry );
		Assert.Equal( tomorrow.GetUtcNow().UtcDateTime, Assert.Single( await GetRunsAsync( schedule ) ).Started,
					TimeSpan.FromMilliseconds( 1 ) );
	}

	[Fact]
	public async Task NothingDueYieldsNoClaim()
	{
		await InsertScheduleAsync( Now.AddHours( 1 ) );

		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );
	}

	[Fact]
	public async Task ConcurrentCoordinatorsAdmitASingleClaim()
	{
		string schedule = await InsertDueScheduleAsync();

		ExecutionCoordinator second =
			CreateCoordinator( "run2", SecurityManagerFactory.CreateAlwaysAuthorized( "worker-b" ) );

		ClaimedJob?[] claims = await Task.WhenAll(
				_coordinator.ClaimDueJobAsync( "worker-a", 1, _jobType ),
				second.ClaimDueJobAsync( "worker-b", 1, _jobType ) );

		Assert.Equal( 1, claims.Count( job => job?.Context.Schedule == schedule ) );
		Assert.Single( await GetRunsAsync( schedule ) );
	}

	[Fact]
	public async Task ManyCoordinatorsRacingOverManySchedulesClaimEachExactlyOnce()
	{
		const int scheduleCount = 12;
		const int coordinatorCount = 6;

		HashSet<string> schedules = new();

		for( int index = 0; index < scheduleCount; index++ )
			schedules.Add( await InsertScheduleAsync( Now.AddSeconds( -1 - index ) ) );

		// Every coordinator sees every due schedule, so one coming back empty means all are taken.
		ExecutionCoordinatorOptions options = new() { ClaimCandidates = scheduleCount };

		TaskCompletionSource start = new( TaskCreationOptions.RunContinuationsAsynchronously );

		// Each claim of a racer is made by a fresh worker number: a holder re-takes its own claim,
		// so one worker claiming repeatedly would get its first job back forever. The bound stops a
		// racer that claims more than there is; the count assertion then fails.
		Task<List<(string Holder, ClaimedJob Job)>>[] racers =
			Enumerable.Range( 0, coordinatorCount )
					.Select(
							index => Task.Run(
									async () =>
									{
										string claimant = $"racer-{index}";

										ExecutionCoordinator coordinator = CreateCoordinator(
												$"run-racer-{index}",
												SecurityManagerFactory.CreateAlwaysAuthorized( claimant ), options );

										List<(string, ClaimedJob)> claimed = new();

										await start.Task;

										for( int worker = 1; worker <= scheduleCount + 1; worker++ )
										{
											ClaimedJob? job = await coordinator.ClaimDueJobAsync( claimant, worker, _jobType );

											if( job is null )
												break;

											claimed.Add( ( $"{claimant}/{worker}", job ) );
										}

										return claimed;
									} ) )
					.ToArray();

		start.SetResult();

		(string Holder, ClaimedJob Job)[] claims =
			( await Task.WhenAll( racers ) ).SelectMany( claimed => claimed ).ToArray();

		Assert.Equal( scheduleCount, claims.Length );
		Assert.True( schedules.SetEquals( claims.Select( claim => claim.Job.Context.Schedule ) ) );

		foreach( (string holder, ClaimedJob job) in claims )
		{
			ScheduleRun run = Assert.Single( await GetRunsAsync( job.Context.Schedule ) );

			Assert.Equal( job.Context.Run, run.Identifier );
			Assert.Equal( holder, run.ClaimedBy );
			Assert.Equal( holder, ( await GetScheduleAsync( job.Context.Schedule ) ).ExecutionLease.ClaimedBy );
		}
	}

	[Fact]
	public async Task WorkersOfOneClaimantNeverShareAClaim()
	{
		string schedule = await InsertDueScheduleAsync();

		// Same group identity, different worker numbers: the lease is per worker, so only one of
		// them may hold the schedule.
		ClaimedJob?[] claims = await Task.WhenAll(
				_coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ),
				_coordinator.ClaimDueJobAsync( Claimant, 2, _jobType ) );

		ClaimedJob job = Assert.Single( claims.OfType<ClaimedJob>() );

		Assert.Equal( schedule, job.Context.Schedule );
		Assert.Single( await GetRunsAsync( schedule ) );

		int winner = Array.IndexOf( claims, job ) + 1;

		Assert.Equal( $"{Claimant}/{winner}", ( await GetScheduleAsync( schedule ) ).ExecutionLease.ClaimedBy );

		// The loser keeps coming back empty while the winner holds the claim.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 3 - winner, _jobType ) );
	}

	// A restarted worker presents the same claimant and number while its predecessor's lease is
	// still live: it is the same holder, so it adopts the unfinished run at once instead of waiting
	// the lease out, and never records the occurrence twice (D20).
	[Fact]
	public async Task RestartedHolderRetakesLiveClaim()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob original = await ClaimSingleAsync( schedule, worker: 3 );

		_clock.Advance( TimeSpan.FromSeconds( 30 ) );

		ClaimedJob retaken = await ClaimSingleAsync( schedule, worker: 3 );

		Assert.Equal( original.Context.Run, retaken.Context.Run );
		Assert.Equal( original.Context.ScheduledFor, retaken.Context.ScheduledFor );
		Assert.Equal( Now + LeaseDuration, retaken.ClaimExpiry );
		Assert.Single( await GetRunsAsync( schedule ) );

		// Another worker of the same group is still excluded, and the restarted one renews.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 4, _jobType ) );
		Assert.True( await _coordinator.RenewClaimAsync( retaken.Context.Run, Claimant, 3 ) );
	}

	// A schedule a live peer holds never reaches the claim at all: even a one-candidate window
	// passes over it to the next due schedule.
	[Fact]
	public async Task ClaimPassesOverAScheduleHeldByALivePeer()
	{
		string contested = await InsertScheduleAsync( Now.AddMinutes( -2 ) );
		string next = await InsertDueScheduleAsync();

		ExecutionCoordinator peer = CreateCoordinator( "run-peer", SecurityManagerFactory.CreateAlwaysAuthorized( "peer" ) );

		await ClaimSingleAsync( contested, coordinator: peer, claimant: "peer" );

		ExecutionCoordinator narrow = CreateCoordinator(
				"run-narrow", SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ),
				new ExecutionCoordinatorOptions { ClaimCandidates = 1 } );

		await ClaimSingleAsync( next, coordinator: narrow );

		Assert.Equal( "peer/1", ( await GetScheduleAsync( contested ) ).ExecutionLease.ClaimedBy );
		Assert.Single( await GetRunsAsync( contested ) );
	}

	[Fact]
	public async Task JobTypeFilterClaimsOnlyThatType()
	{
		string otherType = $"{_jobType}-other";

		string ofThisType = await InsertScheduleAsync( Now.AddMinutes( -2 ) );
		string ofOtherType = await InsertScheduleAsync( Now.AddSeconds( -1 ), jobType: otherType );

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
		string first = await InsertScheduleAsync( Now.AddDays( -2 ) );
		string second = await InsertScheduleAsync( Now.AddDays( -1 ), jobType: otherType );

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

	// #7: one schedule that cannot be claimed (here a schedule stalled before schema v3, whose
	// next occurrence was already recorded as finished) is logged and passed over; the rest of the
	// window is still claimed.
	[Fact]
	public async Task FailingCandidateDoesNotBlockTheWindow()
	{
		DateTime stalledOccurrence = Now.AddMinutes( -2 );

		string broken = await InsertScheduleAsync( stalledOccurrence );
		string healthy = await InsertDueScheduleAsync();

		ScheduleRun finished = new()
		{
			Identifier = $"run-{Guid.NewGuid():N}",
			Schedule = broken,
			Domain = Domain,
			ScheduledFor = stalledOccurrence,
			Started = stalledOccurrence,
			Finished = stalledOccurrence,
			Attempt = 0,
			Trigger = RunTriggers.Schedule,
			Result = RunResults.Succeeded
		};

		await _scheduleDatabase.ApplicationData.WriteAsync( ( session, context ) => session.InsertRunAsync( context ), finished );

		await ClaimSingleAsync( healthy );

		// The broken schedule's failed claim rolled back whole: no lease, no second run.
		Assert.Null( ( await GetScheduleAsync( broken ) ).ExecutionLease );
		Assert.Single( await GetRunsAsync( broken ) );
	}

	// == Complete ==

	[Fact]
	public async Task SuccessfulCompletionAdvancesScheduleAndReleasesClaim()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		JobOutcome outcome = new() { Successful = true, Details = /*lang=json*/ """{"artifact":"blob-1"}""" };

		await _coordinator.ReportCompletionAsync( job.Context.Run, Claimant, 1, RunResults.Succeeded, outcome );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( Now, run.Finished );
		Assert.Equal( RunResults.Succeeded, run.Result );
		Assert.True( run.Outcome!.Successful );

		ScheduleInfo state = await GetScheduleAsync( schedule );

		Assert.Equal( 0, state.State.Attempt );
		Assert.Null( state.State.Occurrence );
		Assert.Equal( RunResults.Succeeded, state.State.LastRun!.Result );
		Assert.Equal( Now.AddHours( 1 ), state.State.NextRun );
		Assert.Null( state.ExecutionLease );
	}

	[Fact]
	public async Task FailureSchedulesRetryPreservingOriginalOccurrence()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob first = await ClaimSingleAsync( schedule );

		DateTime originalOccurrence = first.Context.ScheduledFor;

		await CompleteAsync( first, result: RunResults.Failed );

		ScheduleInfo afterFailure = await GetScheduleAsync( schedule );

		// First retry backs off by the base backoff (1 minute), and the schedule carries the
		// occurrence being retried.
		Assert.Equal( 1, afterFailure.State.Attempt );
		Assert.Equal( originalOccurrence, afterFailure.State.Occurrence );
		Assert.Equal( Now.AddMinutes( 1 ), afterFailure.State.NextRun );

		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );

		_clock.Advance( TimeSpan.FromMinutes( 1 ) );

		ClaimedJob retry = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Retry, retry.Context.Trigger );
		Assert.Equal( 1, retry.Context.Attempt );
		Assert.Equal( originalOccurrence, retry.Context.ScheduledFor );

		Assert.Equal( 2, ( await GetRunsAsync( schedule ) ).Length );
	}

	[Fact]
	public async Task ExhaustedRetriesRecordTerminalFailureAndAdvance()
	{
		string schedule = await InsertDueScheduleAsync( maximumAttempts: 2 );

		await CompleteAsync( await ClaimSingleAsync( schedule ), result: RunResults.Failed );

		_clock.Advance( TimeSpan.FromMinutes( 1 ) );

		await CompleteAsync( await ClaimSingleAsync( schedule ), result: RunResults.TimedOut );

		ScheduleInfo state = await GetScheduleAsync( schedule );

		// Terminal: attempt resets and the schedule advances to its next occurrence.
		Assert.Equal( 0, state.State.Attempt );
		Assert.Null( state.State.Occurrence );
		Assert.Equal( RunResults.TimedOut, state.State.LastRun!.Result );
		Assert.Equal( Now.AddHours( 1 ), state.State.NextRun );
	}

	[Fact]
	public async Task ReportingUnknownRunThrows()
	{
		await Assert.ThrowsAsync<UnknownRunException>(
				() => _coordinator.ReportCompletionAsync(
						"missing", Claimant, 1, RunResults.Succeeded, new JobOutcome { Successful = true } ) );
	}

	[Fact]
	public async Task CompletionFromAWorkerThatNeverClaimedTheRunIsRefused()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule, worker: 1 );

		await Assert.ThrowsAsync<ClaimNotHeldException>( () => CompleteAsync( job, worker: 2 ) );

		Assert.Null( Assert.Single( await GetRunsAsync( schedule ) ).Finished );
		Assert.Equal( $"{Claimant}/1", ( await GetScheduleAsync( schedule ) ).ExecutionLease.ClaimedBy );
	}

	[Fact]
	public async Task SecondCompletionOfAFinishedRunIsRefusedWithoutAdvancingTheSchedule()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		await CompleteAsync( job );

		ScheduleInfo advanced = await GetScheduleAsync( schedule );

		_clock.Advance( TimeSpan.FromMinutes( 1 ) );

		await Assert.ThrowsAsync<ClaimNotHeldException>( () => CompleteAsync( job, result: RunResults.Failed ) );

		ScheduleInfo after = await GetScheduleAsync( schedule );

		Assert.Equal( advanced.State, after.State );
		Assert.Equal( RunResults.Succeeded, Assert.Single( await GetRunsAsync( schedule ) ).Result );
	}

	// The schedule row is locked by the completion, so it computes the next occurrence from the
	// recurrence as it stands — including a change made while the run executed (F3).
	[Fact]
	public async Task RecurrenceChangeDuringRunWinsAtCompletion()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		ScheduleDefinition threeHourly = NewDefinition( _jobType, interval: TimeSpan.FromHours( 3 ) );

		await _scheduler.UpdateDefinitionAsync( schedule, threeHourly );

		_clock.Advance( TimeSpan.FromMinutes( 1 ) );

		await CompleteAsync( job );

		Assert.Equal( Now.AddHours( 3 ), ( await GetScheduleAsync( schedule ) ).State.NextRun );
	}

	// == Misfire ==

	[Fact]
	public async Task MisfireSkipRecordsSkippedRunAndAdvances()
	{
		string schedule = await InsertScheduleAsync( Now.AddMinutes( -10 ), MisfireHandlings.Skip );

		// The only due occurrence was disposed of without execution.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 5, _jobType ) );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( RunResults.Skipped, run.Result );
		Assert.Equal( RunTriggers.Misfire, run.Trigger );
		Assert.Equal( $"{Claimant}/5", run.ClaimedBy );
		Assert.Null( run.Outcome );

		ScheduleInfo state = await GetScheduleAsync( schedule );

		Assert.Equal( Now.AddHours( 1 ), state.State.NextRun );
		Assert.Equal( RunResults.Skipped, state.State.LastRun!.Result );
		Assert.Null( state.ExecutionLease );
	}

	[Fact]
	public async Task OverdueOccurrenceBeyondThresholdIsClassifiedAsMisfire()
	{
		string schedule = await InsertScheduleAsync( Now.AddMinutes( -10 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		// FIRE_ONCE coalesces: the occurrence executes now, flagged as a misfire catch-up.
		Assert.Equal( RunTriggers.Misfire, job.Context.Trigger );
	}

	// #7: a SKIP schedule whose worker crashed mid-run, left unclaimed past the misfire threshold.
	// The run already started, so it is adopted, not skipped: SKIP would have recorded the same
	// occurrence a second time.
	[Fact]
	public async Task MisfireSkipAdoptsOrphanFirst()
	{
		string schedule = await InsertDueScheduleAsync( MisfireHandlings.Skip );

		ClaimedJob original = await ClaimSingleAsync( schedule, worker: 1 );

		_clock.Advance( TimeSpan.FromMinutes( 10 ) );

		ClaimedJob adopted = await ClaimSingleAsync( schedule, worker: 2 );

		Assert.Equal( original.Context.Run, adopted.Context.Run );
		Assert.Equal( original.Context.ScheduledFor, adopted.Context.ScheduledFor );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Null( run.Result );
		Assert.Equal( $"{Claimant}/2", run.ClaimedBy );
	}

	// == Adopt and renew ==

	[Fact]
	public async Task OrphanedRunOfLapsedClaimIsAdopted()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob original = await ClaimSingleAsync( schedule, worker: 1 );

		// A live claim is not adopted by a peer; a lapsed one (a crashed worker) is.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 2, _jobType ) );

		LapseClaims();

		ClaimedJob adopted = await ClaimSingleAsync( schedule, worker: 2 );

		Assert.Equal( original.Context.Run, adopted.Context.Run );
		Assert.Equal( original.Context.Trigger, adopted.Context.Trigger );
		Assert.Equal( Now + LeaseDuration, adopted.ClaimExpiry );

		// The run keeps its record; the claim on it, and its audit holder, move to the adopter.
		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( $"{Claimant}/2", run.ClaimedBy );
		Assert.Equal( $"{Claimant}/2", ( await GetScheduleAsync( schedule ) ).ExecutionLease.ClaimedBy );
	}

	[Fact]
	public async Task ClaimRenewalTracksLeaseLifecycle()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		_clock.Advance( TimeSpan.FromSeconds( 30 ) );

		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );
		Assert.Equal( Now + LeaseDuration, ( await GetScheduleAsync( schedule ) ).ExecutionLease.Expiry );

		// The claim is the worker's, not its group's.
		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 2 ) );

		await CompleteAsync( job );

		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );
	}

	[Fact]
	public async Task RenewAfterPeerAdoptionFails()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule, worker: 1 );

		LapseClaims();

		await ClaimSingleAsync( schedule, worker: 2 );

		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );
		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 2 ) );
	}

	[Fact]
	public async Task LapsedClaimNotAdoptedStillBelongsToItsHolder()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		LapseClaims();

		// Nobody adopted the run, so it is still the holder's own: it renews and completes it.
		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );

		LapseClaims();

		await CompleteAsync( job );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.NotNull( run.Finished );
		Assert.Equal( RunResults.Succeeded, run.Result );
	}

	[Fact]
	public async Task CompletionFromAWorkerThatLostTheRunToAdoptionIsRefused()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule, worker: 1 );

		LapseClaims();

		await ClaimSingleAsync( schedule, worker: 2 );

		ScheduleInfo adopted = await GetScheduleAsync( schedule );

		ClaimNotHeldException exception =
			await Assert.ThrowsAsync<ClaimNotHeldException>( () => CompleteAsync( job, worker: 1 ) );

		Assert.Equal( job.Context.Run, exception.Run );
		Assert.Null( Assert.Single( await GetRunsAsync( schedule ) ).Finished );
		ScheduleInfo after = await GetScheduleAsync( schedule );

		Assert.Equal( adopted.State, after.State );
		Assert.Equal( adopted.ExecutionLease, after.ExecutionLease );

		// The adopter still holds the claim and can complete.
		await CompleteAsync( job, worker: 2 );

		Assert.NotNull( Assert.Single( await GetRunsAsync( schedule ) ).Finished );
	}

	// A damaged row naming a finished run as in progress (the run finished, the schedule never
	// recorded it) is repaired by the claim that meets it: the run's completion is applied, rather
	// than the run being executed again or its occurrence recorded twice.
	[Fact]
	public async Task AdoptingAFinishedRunRecordsItsCompletionInstead()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		await _scheduleDatabase.ApplicationData.WriteAsync(
				( session, context ) => session.FinishRunAsync( context, Now, RunResults.Succeeded, null ),
				job.Context.Run );

		LapseClaims();

		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 2, _jobType ) );

		ScheduleInfo repaired = await GetScheduleAsync( schedule );

		Assert.Null( repaired.ExecutionLease );
		Assert.Equal( Now.AddHours( 1 ), repaired.State.NextRun );
		Assert.Equal( RunResults.Succeeded, repaired.State.LastRun!.Result );
		Assert.Equal( $"{Claimant}/1", Assert.Single( await GetRunsAsync( schedule ) ).ClaimedBy );
	}

	// == Manual runs ==

	[Fact]
	public async Task RequestedRunIsClassifiedAsManualAndConsumesTheRequest()
	{
		DateTime nextRun = Now.AddHours( 1 );

		string schedule = await InsertScheduleAsync( nextRun );

		await _scheduler.RunNowAsync( schedule );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Manual, job.Context.Trigger );
		Assert.Equal( Now, job.Context.ScheduledFor );
		Assert.Equal( RunTriggers.Manual, Assert.Single( await GetRunsAsync( schedule ) ).Trigger );

		// The request is consumed by the claim that honored it; the next occurrence never moved.
		ScheduleInfo state = await GetScheduleAsync( schedule );

		Assert.False( state.State.RunRequested );
		Assert.Equal( nextRun, state.State.NextRun );

		await CompleteAsync( job );

		Assert.Equal( nextRun, ( await GetScheduleAsync( schedule ) ).State.NextRun );
	}

	// F1: a run-now while a run executes is refused, so it cannot move the occurrence; a crash of
	// that run then leads to its adoption, never to its occurrence being recorded again.
	[Fact]
	public async Task RunNowDuringRunIsRefused()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule, worker: 1 );

		ScheduleInfo before = await GetScheduleAsync( schedule );

		RunInProgressException refusal =
			await Assert.ThrowsAsync<RunInProgressException>( () => _scheduler.RunNowAsync( schedule ) );

		Assert.Equal( schedule, refusal.Schedule );
		Assert.Equal( job.Context.Run, refusal.Run );
		Assert.Equal( $"{Claimant}/1", refusal.Holder );
		Assert.Equal( job.ClaimExpiry, refusal.LeaseExpiry );

		ScheduleInfo after = await GetScheduleAsync( schedule );

		Assert.Equal( before.State, after.State );
		Assert.Equal( before.ExecutionLease, after.ExecutionLease );

		LapseClaims();

		ClaimedJob adopted = await ClaimSingleAsync( schedule, worker: 2 );

		Assert.Equal( job.Context.Run, adopted.Context.Run );
		Assert.Single( await GetRunsAsync( schedule ) );
	}

	[Fact]
	public async Task RunNowWhileLapsedIsRefused()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		LapseClaims();

		RunInProgressException refusal =
			await Assert.ThrowsAsync<RunInProgressException>( () => _scheduler.RunNowAsync( schedule ) );

		Assert.Equal( job.Context.Run, refusal.Run );
		Assert.False( ( await GetScheduleAsync( schedule ) ).State.RunRequested );
	}

	// RUN_ALL executes every missed occurrence: a manual run in the middle of a backlog leaves the
	// backlog to be worked through.
	[Fact]
	public async Task ManualRunKeepsRunAllBacklog()
	{
		DateTime backlog = Now.AddHours( -3 );

		string schedule = await InsertScheduleAsync( backlog, MisfireHandlings.RunAll );

		await _scheduler.RunNowAsync( schedule );

		ClaimedJob manual = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Manual, manual.Context.Trigger );

		await CompleteAsync( manual );

		Assert.Equal( backlog, ( await GetScheduleAsync( schedule ) ).State.NextRun );

		ClaimedJob caughtUp = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Misfire, caughtUp.Context.Trigger );
		Assert.Equal( backlog, caughtUp.Context.ScheduledFor );
	}

	// FIRE_ONCE (and SKIP) coalesce: an occurrence already due when the manual run started is
	// satisfied by it.
	[Fact]
	public async Task ManualRunCoalescesDueBacklogUnderFireOnce()
	{
		string schedule = await InsertScheduleAsync( Now.AddHours( -3 ) );

		await _scheduler.RunNowAsync( schedule );

		ClaimedJob manual = await ClaimSingleAsync( schedule );

		_clock.Advance( TimeSpan.FromMinutes( 1 ) );

		await CompleteAsync( manual );

		Assert.Equal( Now.AddHours( 1 ), ( await GetScheduleAsync( schedule ) ).State.NextRun );
		Assert.Null( await _coordinator.ClaimDueJobAsync( Claimant, 1, _jobType ) );
	}

	[Fact]
	public async Task ManualRunFailureIsNotRetried()
	{
		DateTime nextRun = Now.AddHours( 1 );

		string schedule = await InsertScheduleAsync( nextRun );

		await _scheduler.RunNowAsync( schedule );

		await CompleteAsync( await ClaimSingleAsync( schedule ), result: RunResults.Failed );

		ScheduleInfo state = await GetScheduleAsync( schedule );

		Assert.Equal( 0, state.State.Attempt );
		Assert.Null( state.State.Occurrence );
		Assert.Equal( nextRun, state.State.NextRun );
		Assert.Equal( RunResults.Failed, state.State.LastRun!.Result );
	}

	// A manual run during a retry's backoff is that retry, brought forward: same occurrence, same
	// attempt, and a failure carries on the retry sequence.
	[Fact]
	public async Task ManualRunDuringBackoffAcceleratesRetry()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob first = await ClaimSingleAsync( schedule );

		await CompleteAsync( first, result: RunResults.Failed );

		await _scheduler.RunNowAsync( schedule );

		ClaimedJob accelerated = await ClaimSingleAsync( schedule );

		Assert.Equal( RunTriggers.Manual, accelerated.Context.Trigger );
		Assert.Equal( 1, accelerated.Context.Attempt );
		Assert.Equal( first.Context.ScheduledFor, accelerated.Context.ScheduledFor );

		await CompleteAsync( accelerated, result: RunResults.Failed );

		ScheduleInfo state = await GetScheduleAsync( schedule );

		Assert.Equal( 2, state.State.Attempt );
		Assert.Equal( first.Context.ScheduledFor, state.State.Occurrence );
		Assert.Equal( Now.AddMinutes( 2 ), state.State.NextRun );
	}

	// == Delete ==

	[Fact]
	public async Task DeletedScheduleRenewFailsAndCompletionIsUnknownRun()
	{
		string schedule = await InsertDueScheduleAsync();

		ClaimedJob job = await ClaimSingleAsync( schedule );

		await _scheduler.DeleteAsync( schedule );

		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant, 1 ) );
		await Assert.ThrowsAsync<UnknownRunException>( () => CompleteAsync( job ) );
	}

	// == Authorization ==

	[Fact]
	public async Task UngrantedClaimantIsRefusedEveryOperation()
	{
		string schedule = await InsertDueScheduleAsync();

		// Authorization belongs to the coordinator, not to any hosting surface: an in-process
		// worker is held to the same grants as one calling in over a transport.
		ExecutionCoordinator denied = CreateCoordinator(
				"run-denied", SecurityManagerFactory.CreateAlwaysDenied( "unauthorized-worker" ) );

		await Assert.ThrowsAsync<NotAuthorized>( () => denied.ClaimDueJobAsync( "unauthorized-worker", 1, _jobType ) );

		// The refused claim must not have taken the schedule from a legitimate worker.
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
		string schedule = await InsertDueScheduleAsync();

		// No principal identifies an in-process caller: endpoints reject unauthenticated requests
		// before dispatch, so a call arriving without one cannot have crossed a transport. An
		// in-process worker therefore needs no service identity, and holds no grants.
		ExecutionCoordinator inProcess =
			CreateCoordinator( "run-in-process", SecurityManagerFactory.CreateWithoutPrincipal() );

		ClaimedJob job = await ClaimSingleAsync( schedule, coordinator: inProcess, claimant: "in-process-worker" );

		Assert.True( await inProcess.RenewClaimAsync( job.Context.Run, "in-process-worker", 1 ) );

		await inProcess.ReportCompletionAsync(
				job.Context.Run, "in-process-worker", 1, RunResults.Succeeded,
				new JobOutcome { Successful = true } );

		Assert.Equal( RunResults.Succeeded, Assert.Single( await GetRunsAsync( schedule ) ).Result );
	}

	// == Races ==

	// A worker whose claim lapsed while its run was executing reports completion at the moment a
	// peer claims the schedule and would adopt that unfinished run. Both lock the schedule row, so
	// exactly one wins: the completion (the peer then finds nothing to do), or the adoption (the
	// late completion is then refused). The loser must never see a raw store failure instead — on
	// Postgres, a serialization failure (40001) — and a finished run must never be adopted.
	[Fact]
	public async Task LateCompletionRacingAdoptionHasExactlyOneWinner()
	{
		const int iterations = 60;
		const string completer = "late-completer";
		const string adopter = "adopter";

		string completerHolder = $"{completer}/1";
		string adopterHolder = $"{adopter}/1";

		ExecutionCoordinator completing =
			CreateCoordinator( "run-completer", SecurityManagerFactory.CreateAlwaysAuthorized( completer ) );

		ExecutionCoordinator adopting =
			CreateCoordinator( "run-adopter", SecurityManagerFactory.CreateAlwaysAuthorized( adopter ) );

		int completed = 0;
		int adopted = 0;
		List<string> failures = new();

		for( int iteration = 0; iteration < iterations; iteration++ )
		{
			string schedule = await InsertDueScheduleAsync();

			string run = ( await ClaimSingleAsync( schedule, coordinator: completing, claimant: completer ) ).Context.Run;

			// The completer's claim lapses while its run is still executing.
			LapseClaims();

			TimeSpan stagger = Stagger( iteration );

			(Exception? completionError, Exception? claimError, ClaimedJob? claimed) = await RaceAsync(
					stagger,
					() => completing.ReportCompletionAsync(
							run, completer, 1, RunResults.Succeeded, new JobOutcome { Successful = true } ),
					() => adopting.ClaimDueJobAsync( adopter, 1, _jobType ) );

			ScheduleRun[] runs = await GetRunsAsync( schedule );
			ScheduleRun? record = runs.Length == 1 ? runs[0] : null;
			ExecutionLease? lease = ( await GetScheduleAsync( schedule ) ).ExecutionLease;

			bool completionWon =
				completionError is null && claimError is null && claimed is null &&
				record?.Finished is not null && record.ClaimedBy == completerHolder && lease is null;

			bool adoptionWon =
				completionError is ClaimNotHeldException && claimError is null &&
				claimed?.Context.Run == run &&
				record is not null && record.Finished is null && record.ClaimedBy == adopterHolder &&
				lease?.ClaimedBy == adopterHolder;

			if( completionWon )
				completed++;
			else if( adoptionWon )
				adopted++;
			else
				failures.Add(
						$"iteration {iteration} (stagger {stagger.TotalMilliseconds} ms): " +
						$"completion {Describe( completionError )}; " +
						$"claim {( claimError is null ? $"returned run {claimed?.Context.Run ?? "none"}" : Describe( claimError ) )}; " +
						$"lease {lease?.ClaimedBy ?? "none"}; {runs.Length} run row(s): " +
						string.Join(
								", ",
								runs.Select(
										row => $"{row.Identifier} claimed by {row.ClaimedBy}, " +
												$"{( row.Finished is null ? "unfinished" : "finished" )}" ) ) );

			await DeleteScheduleAsync( schedule );
		}

		Assert.True( failures.Count == 0, string.Join( Environment.NewLine, failures ) );

		// Both orderings must actually have been exercised, or the test proves nothing.
		Assert.True( completed > 0, $"The completion never won ({adopted} adoptions)." );
		Assert.True( adopted > 0, $"The adoption never won ({completed} completions)." );
	}

	// The management facade writes the same schedule row the coordinator's completion advances:
	// pausing, requesting a run, or updating the definition while a run of the schedule is being
	// completed. Neither side may fail for the other's write — on Postgres, no serialization
	// failure (40001) — and both take effect. The one deliberate exception: a run-now that lands
	// while the run is still in progress is refused (RunInProgressException), and leaves nothing.
	[Fact]
	public async Task ManagementWritesRacingACompletionNeitherFails()
	{
		const int iterations = 60;

		List<string> failures = new();

		for( int iteration = 0; iteration < iterations; iteration++ )
		{
			string schedule = await InsertDueScheduleAsync();

			ClaimedJob job = await ClaimSingleAsync( schedule );

			ScheduleDefinition renamed = NewDefinition( _jobType ) with { Name = $"Renamed {iteration}" };

			int operation = iteration % 3;

			Func<Task> manage = operation switch
			{
				0 => () => _scheduler.PauseAsync( schedule ),
				1 => () => _scheduler.RunNowAsync( schedule ),
				_ => () => _scheduler.UpdateDefinitionAsync( schedule, renamed )
			};

			TimeSpan stagger = Stagger( iteration / 3 );

			(Exception? completionError, Exception? managementError, _) = await RaceAsync(
					stagger, () => CompleteAsync( job ),
					async () =>
					{
						await manage();

						return (ClaimedJob?)null;
					} );

			ScheduleInfo after = await GetScheduleAsync( schedule );
			ScheduleRun[] runs = await GetRunsAsync( schedule );

			// The completion applied: its run finished, the schedule recorded it and advanced.
			bool completed = runs.Length == 1 && runs[0].Finished is not null &&
							after.State.LastRun?.Result == RunResults.Succeeded &&
							after.State.NextRun == Now.AddHours( 1 ) && after.ExecutionLease is null;

			bool managed = operation switch
			{
				0 => managementError is null && after.State.Paused,
				1 => managementError is null
						? after.State.RunRequested
						: managementError is RunInProgressException && !after.State.RunRequested,
				_ => managementError is null && after.Definition.Name == renamed.Name
			};

			if( completionError is not null || !completed || !managed )
				failures.Add(
						$"iteration {iteration} ({( operation == 0 ? "pause" : operation == 1 ? "run now" : "update definition" )}, " +
						$"stagger {stagger.TotalMilliseconds} ms): completion {Describe( completionError )}; " +
						$"management {Describe( managementError )}; completion applied {completed}; " +
						$"management applied {managed}" );

			await DeleteScheduleAsync( schedule );
		}

		Assert.True( failures.Count == 0, string.Join( Environment.NewLine, failures ) );
	}

	// #8: a definition update reads the schedule before it writes. A completion committed in
	// between must not be reverted: the update decides the next occurrence on the row it locked.
	[Fact]
	public Task UpdateDefinitionRacingCompletionNeverRevertsNextRun() =>
		DefinitionUpdatesRacingCompletionsAsync( TimeSpan.FromHours( 1 ) );

	// F3 under a race: whichever commits last, the next occurrence follows the new recurrence.
	[Fact]
	public Task RecurrenceChangeRacingCompletionAlwaysWins() =>
		DefinitionUpdatesRacingCompletionsAsync( TimeSpan.FromHours( 3 ) );

	private async Task DefinitionUpdatesRacingCompletionsAsync( TimeSpan updatedInterval )
	{
		const int iterations = 60;

		List<string> failures = new();

		for( int iteration = 0; iteration < iterations; iteration++ )
		{
			string schedule = await InsertDueScheduleAsync();

			ClaimedJob job = await ClaimSingleAsync( schedule );

			ScheduleDefinition updated =
				NewDefinition( _jobType, interval: updatedInterval ) with { Name = $"Renamed {iteration}" };

			TimeSpan stagger = Stagger( iteration );

			(Exception? completionError, Exception? updateError, _) = await RaceAsync(
					stagger, () => CompleteAsync( job ),
					async () =>
					{
						await _scheduler.UpdateDefinitionAsync( schedule, updated );

						return (ClaimedJob?)null;
					} );

			ScheduleInfo after = await GetScheduleAsync( schedule );

			if( completionError is not null || updateError is not null || after.State.NextRun != Now + updatedInterval ||
				after.Definition.Name != updated.Name )
				failures.Add(
						$"iteration {iteration} (stagger {stagger.TotalMilliseconds} ms): " +
						$"completion {Describe( completionError )}; update {Describe( updateError )}; " +
						$"next run {after.State.NextRun:O} (occurrence {job.Context.ScheduledFor:O}, " +
						$"expected {Now + updatedInterval:O}); name {after.Definition.Name}" );

			await DeleteScheduleAsync( schedule );
		}

		Assert.True( failures.Count == 0, string.Join( Environment.NewLine, failures ) );
	}

	// The start of one side is held back by a stagger swept across the iterations (positive: the
	// first waits, negative: the second waits), so the two land in both orders and, around zero,
	// while the other is in flight.
	private static TimeSpan Stagger( int iteration ) => TimeSpan.FromMilliseconds( ( iteration % 9 - 4 ) * 0.5 );

	private static async Task<(Exception? First, Exception? Second, ClaimedJob? Claimed)> RaceAsync(
		TimeSpan stagger, Func<Task> first, Func<Task<ClaimedJob?>> second )
	{
		TaskCompletionSource start = new( TaskCreationOptions.RunContinuationsAsynchronously );

		Task firstTask = Task.Run(
				async () =>
				{
					await start.Task;

					SpinFor( stagger );

					await first();
				} );

		Task<ClaimedJob?> secondTask = Task.Run(
				async () =>
				{
					await start.Task;

					SpinFor( -stagger );

					return await second();
				} );

		start.SetResult();

		Exception? firstError = await CaptureAsync( firstTask );
		Exception? secondError = await CaptureAsync( secondTask );

		return ( firstError, secondError, secondError is null ? await secondTask : null );
	}

	private static void SpinFor( TimeSpan duration )
	{
		if( duration <= TimeSpan.Zero )
			return;

		Stopwatch stopwatch = Stopwatch.StartNew();

		while( stopwatch.Elapsed < duration )
			Thread.SpinWait( 20 );
	}

	private static async Task<Exception?> CaptureAsync( Task task )
	{
		try
		{
			await task;

			return null;
		}
		catch( Exception exception )
		{
			return exception;
		}
	}

	private static string Describe( Exception? exception ) =>
		exception is null ? "succeeded" : $"threw {exception.GetType().Name}: {exception.Message}";
}

public sealed class SqliteExecutionCoordinatorTests
	: ExecutionCoordinatorTests<SqliteScheduleDatabaseFixture>, IClassFixture<SqliteScheduleDatabaseFixture>
{
	public SqliteExecutionCoordinatorTests( SqliteScheduleDatabaseFixture scheduleDatabase ) : base( scheduleDatabase )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresExecutionCoordinatorTests
	: ExecutionCoordinatorTests<PostgresScheduleDatabaseFixture>, IClassFixture<PostgresScheduleDatabaseFixture>
{
	public PostgresExecutionCoordinatorTests( PostgresScheduleDatabaseFixture scheduleDatabase ) : base( scheduleDatabase )
	{
	}
}
