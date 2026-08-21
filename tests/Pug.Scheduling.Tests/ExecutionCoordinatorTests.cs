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
		_coordinator = new ExecutionCoordinator(
				new SequentialIdGenerator( "run" ),
				scheduleDatabase.ApplicationData,
				_leaseProvider,
				SecurityManagerFactory.CreateAlwaysAuthorized( Claimant ),
				new ExecutionCoordinatorOptions() );
	}

	private async Task<string> InsertScheduleAsync( DateTime? nextRun, int attempt = 0,
													string misfireHandling = MisfireHandlings.FireOnce,
													TimeSpan? misfireThreshold = null,
													int maximumAttempts = 3,
													TimeSpan? interval = null )
	{
		ScheduleInfo schedule = new()
		{
			Identifier = $"schedule-{Guid.NewGuid():N}",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			Definition = new ScheduleDefinition
			{
				Domain = Domain,
				Name = "Coordinator test schedule",
				JobType = "test",
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

	private async Task<ClaimedJob> ClaimSingleAsync( string schedule )
	{
		ClaimedJob? job = ( await _coordinator.ClaimDueJobsAsync( Claimant, 10 ) )
						.SingleOrDefault( candidate => candidate.Context.Schedule == schedule );

		Assert.NotNull( job );

		return job;
	}

	[Fact]
	public async Task DueScheduleIsClaimedWithRunRecorded()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.Equal( Domain, job.Context.Domain );
		Assert.Equal( "owner-1", job.Context.Owner.Identifier );
		Assert.Equal( "test", job.Context.JobType );
		Assert.Equal( RunTriggers.Schedule, job.Context.Trigger );
		Assert.Equal( 0, job.Context.Attempt );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), job.Timeout );

		ScheduleRun run = Assert.Single( await GetRunsAsync( schedule ) );

		Assert.Equal( job.Context.Run, run.Identifier );
		Assert.Null( run.Finished );

		Assert.NotNull( await _leaseProvider.GetAsync( schedule ) );
	}

	[Fact]
	public async Task NothingDueYieldsNoClaims()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddHours( 1 ) );

		IEnumerable<ClaimedJob> jobs = await _coordinator.ClaimDueJobsAsync( Claimant, 10 );

		Assert.DoesNotContain( jobs, job => job.Context.Schedule == schedule );
	}

	[Fact]
	public async Task ConcurrentCoordinatorsAdmitASingleClaim()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ExecutionCoordinator second = new(
				new SequentialIdGenerator( "run2" ),
				_scheduleDatabase.ApplicationData,
				_leaseProvider,
				SecurityManagerFactory.CreateAlwaysAuthorized( "worker-b" ),
				new ExecutionCoordinatorOptions() );

		IEnumerable<ClaimedJob>[] claims = await Task.WhenAll(
				_coordinator.ClaimDueJobsAsync( "worker-a", 10 ),
				second.ClaimDueJobsAsync( "worker-b", 10 ) );

		int total = claims.SelectMany( jobs => jobs )
						.Count( job => job.Context.Schedule == schedule );

		Assert.Equal( 1, total );
	}

	[Fact]
	public async Task SuccessfulCompletionAdvancesScheduleAndReleasesClaim()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		JobOutcome outcome = new() { Successful = true, Details = /*lang=json*/ """{"artifact":"blob-1"}""" };

		await _coordinator.ReportCompletionAsync( job.Context.Run, Claimant, RunResults.Succeeded, outcome );

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
				first.Context.Run, Claimant, RunResults.Failed,
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
				first.Context.Run, Claimant, RunResults.Failed,
				new JobOutcome { Successful = false, Error = "boom" } );

		await ForceDueAsync( schedule );

		ClaimedJob retry = await ClaimSingleAsync( schedule );

		await _coordinator.ReportCompletionAsync(
				retry.Context.Run, Claimant, RunResults.Failed,
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

		IEnumerable<ClaimedJob> jobs = await _coordinator.ClaimDueJobsAsync( Claimant, 10 );

		Assert.DoesNotContain( jobs, job => job.Context.Schedule == schedule );

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
		await _leaseProvider.ReleaseAsync( schedule, Claimant );

		ClaimedJob readopted = await ClaimSingleAsync( schedule );

		Assert.Equal( original.Context.Run, readopted.Context.Run );
		Assert.Single( await GetRunsAsync( schedule ) );
	}

	[Fact]
	public async Task ClaimRenewalTracksLeaseLifecycle()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		ClaimedJob job = await ClaimSingleAsync( schedule );

		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant ) );

		await _coordinator.ReportCompletionAsync(
				job.Context.Run, Claimant, RunResults.Succeeded, new JobOutcome { Successful = true } );

		Assert.False( await _coordinator.RenewClaimAsync( job.Context.Run, Claimant ) );
	}

	[Fact]
	public async Task ReportingUnknownRunThrows()
	{
		await Assert.ThrowsAsync<UnknownRunException>(
				() => _coordinator.ReportCompletionAsync(
						"missing", Claimant, RunResults.Succeeded, new JobOutcome { Successful = true } ) );
	}

	[Fact]
	public async Task UngrantedClaimantIsRefusedEveryOperation()
	{
		string schedule = await InsertScheduleAsync( DateTime.UtcNow.AddSeconds( -1 ) );

		// Authorization belongs to the coordinator, not to any hosting surface: an in-process
		// worker is held to the same grants as one calling in over a transport.
		ExecutionCoordinator denied = new(
				new SequentialIdGenerator( "run-denied" ),
				_scheduleDatabase.ApplicationData,
				_leaseProvider,
				SecurityManagerFactory.CreateAlwaysDenied( "unauthorized-worker" ),
				new ExecutionCoordinatorOptions() );

		await Assert.ThrowsAsync<NotAuthorized>( () => denied.ClaimDueJobsAsync( "unauthorized-worker", 10 ) );

		// The refused claim must not have leased the schedule away from a legitimate worker.
		ClaimedJob job = Assert.Single(
				await _coordinator.ClaimDueJobsAsync( Claimant, 10 ), claimed => claimed.Context.Schedule == schedule );

		await Assert.ThrowsAsync<NotAuthorized>(
				() => denied.RenewClaimAsync( job.Context.Run, "unauthorized-worker" ) );

		await Assert.ThrowsAsync<NotAuthorized>(
				() => denied.ReportCompletionAsync(
						job.Context.Run, "unauthorized-worker", RunResults.Succeeded,
						new JobOutcome { Successful = true } ) );
	}
}
