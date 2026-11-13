using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Client;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Round-trips <see cref="IExecutionCoordinator"/> through the API client — the binding an
/// out-of-process worker host uses. The claim/renew/report cycle must behave exactly as the
/// in-process coordinator does, since <c>SchedulerWorker</c> is written against the interface and
/// never learns which one it holds.
/// </summary>
public class ExecutionCoordinatorClientTests : IClassFixture<SchedulingApiFixture>
{
	private const string Owner = "coordinator-client-owner";
	private const string Worker = "remote-client-worker";
	private const string OtherWorker = "remote-client-worker-2";

	// Above 1, which the SchedulerWorker below claims as: each test claims as a worker number of
	// its own, so claims another test left live are never handed back to it.
	private static int _lastWorkerNumber = 100;

	private readonly int _worker = Interlocked.Increment( ref _lastWorkerNumber );

	private readonly SchedulingApiFixture _fixture;
	private readonly IScheduler _scheduler;
	private readonly IExecutionCoordinator _coordinator;

	public ExecutionCoordinatorClientTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;
		_scheduler = new SchedulerClient( fixture.CreateApiClient( Owner ) );
		_coordinator = new ExecutionCoordinatorClient( fixture.CreateApiClient( Worker ) );

		foreach( string operation in new[]
				{
					CoordinatorSecurityOperations.ClaimJobs, CoordinatorSecurityOperations.RenewClaim,
					CoordinatorSecurityOperations.ReportCompletion
				} )
			fixture.Authorizations.Grant(
					Worker, operation, SecurityObjectTypes.ExecutionCoordinator, identifier: Worker );
	}

	private async Task<string> RegisterDueScheduleAsync( string name )
	{
		string identifier = await _scheduler.RegisterAsync(
				new ScheduleDefinition
				{
					Domain = SchedulingApiFixture.Domain,
					Name = name,
					JobType = SchedulingApiFixture.JobType,
					Payload = /*lang=json*/ """{"configuration":"report-1"}""",
					Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
				} );

		await _scheduler.RunNowAsync( identifier );

		return identifier;
	}

	[Fact]
	public async Task ClaimRenewAndReportRoundTrip()
	{
		string identifier = await RegisterDueScheduleAsync( "Remotely claimed" );

		ClaimedJob? job = await _coordinator.ClaimDueJobAsync( Worker, _worker, null );

		Assert.NotNull( job );
		Assert.Equal( identifier, job.Context.Schedule );

		// The execution context a worker hands its job handler survives the wire intact.
		Assert.Equal( SchedulingApiFixture.Domain, job.Context.Domain );
		Assert.Equal( SchedulingApiFixture.JobType, job.Context.JobType );
		Assert.Equal( Owner, job.Context.Owner.Identifier );
		Assert.Equal( RunTriggers.Manual, job.Context.Trigger );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), job.Timeout );
		Assert.True( job.ClaimExpiry > DateTime.UtcNow );

		Assert.True( await _coordinator.RenewClaimAsync( job.Context.Run, Worker, _worker ) );

		await _coordinator.ReportCompletionAsync(
				job.Context.Run, Worker, _worker, RunResults.Succeeded,
				new JobOutcome { Successful = true, Details = /*lang=json*/ """{"output":"report-1.pdf"}""" } );

		ScheduleRun run = Assert.Single( await _scheduler.GetRunsAsync( identifier ) );

		Assert.Equal( RunResults.Succeeded, run.Result );
		Assert.True( run.Outcome.Successful );
		Assert.NotNull( run.Finished );

		ScheduleInfo schedule = await _scheduler.GetScheduleAsync( identifier );

		Assert.Null( schedule.ExecutionLease );
		Assert.False( schedule.State.RunRequested );
	}

	[Fact]
	public async Task NothingDueClaimsNothing()
	{
		// Nothing due (204) must come back as null, not as a failure. A job type nothing is
		// scheduled under keeps due work other tests left behind from answering.
		Assert.Null( await _coordinator.ClaimDueJobAsync( Worker, _worker, "no-such-type" ) );
	}

	[Fact]
	public async Task InvalidCallerSurfacesAsArgumentException()
	{
		ArgumentException exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.ClaimDueJobAsync( Worker, 0, null ) );

		Assert.Equal( "worker", exception.ParamName );

		exception = await Assert.ThrowsAsync<ArgumentException>(
				() => _coordinator.RenewClaimAsync( "some-run", $"{Worker}/2", 1 ) );

		Assert.Equal( "claimant", exception.ParamName );
	}

	[Fact]
	public async Task LostClaimIsReportedAsFalseRatherThanAFailure()
	{
		string identifier = await RegisterDueScheduleAsync( "Renewal contested" );

		ClaimedJob? job = await _coordinator.ClaimDueJobAsync( Worker, _worker, null );

		Assert.Equal( identifier, job?.Context.Schedule );

		_fixture.Authorizations.Grant(
				OtherWorker, CoordinatorSecurityOperations.RenewClaim, SecurityObjectTypes.ExecutionCoordinator,
				identifier: OtherWorker );

		IExecutionCoordinator other = new ExecutionCoordinatorClient( _fixture.CreateApiClient( OtherWorker ) );

		Assert.False( await other.RenewClaimAsync( job!.Context.Run, OtherWorker, _worker ) );
	}

	[Fact]
	public async Task UnknownRunSurfacesTheAbstractionsException()
	{
		UnknownRunException exception = await Assert.ThrowsAsync<UnknownRunException>(
				() => _coordinator.ReportCompletionAsync(
						"no-such-run", Worker, _worker, RunResults.Failed, new JobOutcome { Successful = false } ) );

		Assert.Equal( "no-such-run", exception.Identifier );
	}

	[Fact]
	public async Task CompletionFromAWorkerThatDoesNotHoldTheRunSurfacesTheAbstractionsException()
	{
		await RegisterDueScheduleAsync( "Completed by a stranger" );

		ClaimedJob? job = await _coordinator.ClaimDueJobAsync( Worker, _worker, null );

		Assert.NotNull( job );

		ClaimNotHeldException exception = await Assert.ThrowsAsync<ClaimNotHeldException>(
				() => _coordinator.ReportCompletionAsync(
						job.Context.Run, Worker, _worker + 1000, RunResults.Succeeded,
						new JobOutcome { Successful = true } ) );

		Assert.Equal( job.Context.Run, exception.Run );
	}

	[Fact]
	public async Task ActingAsAnotherClaimantSurfacesAsNotAuthorized()
	{
		IExecutionCoordinator impersonating =
			new ExecutionCoordinatorClient( _fixture.CreateApiClient( Worker ) );

		await Assert.ThrowsAsync<NotAuthorized>( () => impersonating.ClaimDueJobAsync( OtherWorker, 1, null ) );
	}

	[Fact]
	public async Task WorkerRunsAgainstTheRemoteCoordinatorUnchanged()
	{
		string identifier = await RegisterDueScheduleAsync( "Executed remotely" );

		ScheduledJobContext? dispatched = null;

		// The deployment this package exists for: a real worker whose only link to the scheduler
		// is the HTTP coordinator client.
		using Worker.SchedulerWorker worker = new(
				_coordinator,
				new DelegateDispatcher(
						( context, _ ) =>
						{
							dispatched = context;

							return Task.FromResult(
									new JobOutcome { Successful = true, Details = /*lang=json*/ """{"sent":true}""" } );
						} ),
				new Worker.WorkerOptions
				{
					Claimant = Worker,
					PollInterval = TimeSpan.FromMilliseconds( 50 ),
					HeartbeatInterval = TimeSpan.FromSeconds( 30 )
				},
				NullLogger<Worker.SchedulerWorker>.Instance );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await WaitForAsync( async () => ( await _scheduler.GetRunsAsync( identifier ) ).Any() );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}

		Assert.NotNull( dispatched );
		Assert.Equal( identifier, dispatched!.Schedule );
		Assert.Equal( Owner, dispatched.Owner.Identifier );

		ScheduleRun run = Assert.Single( await _scheduler.GetRunsAsync( identifier ) );

		Assert.Equal( RunResults.Succeeded, run.Result );
		Assert.True( run.Outcome.Successful );
	}

	private static async Task WaitForAsync( Func<Task<bool>> condition )
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds( 10 );

		while( DateTime.UtcNow < deadline )
		{
			if( await condition() )
				return;

			await Task.Delay( 25 );
		}

		throw new TimeoutException( "Condition was not met before the deadline." );
	}
}
