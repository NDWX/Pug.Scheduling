using Microsoft.Extensions.Logging.Abstractions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Worker;

namespace Pug.Scheduling.Tests;

// Worker pump behavior against fake coordinator/dispatcher: dispatch, outcome classification,
// timeout enforcement, and defensiveness against third-party handler behavior.
public class SchedulerWorkerTests
{
	private sealed class FakeCoordinator : IExecutionCoordinator
	{
		private ClaimedJob? _job;

		public TaskCompletionSource<(string Run, string Result, JobOutcome Outcome)> Reported { get; } =
			new( TaskCreationOptions.RunContinuationsAsynchronously );

		public FakeCoordinator( ClaimedJob job )
		{
			_job = job;
		}

		public Task<IEnumerable<ClaimedJob>> ClaimDueJobsAsync( string claimant, int maximumJobs )
		{
			ClaimedJob? job = Interlocked.Exchange( ref _job, null );

			return Task.FromResult<IEnumerable<ClaimedJob>>( job is null ? Array.Empty<ClaimedJob>() : new[] { job } );
		}

		public Task<bool> RenewClaimAsync( string run, string claimant ) => Task.FromResult( true );

		public Task ReportCompletionAsync( string run, string claimant, string result, JobOutcome outcome )
		{
			Reported.TrySetResult( ( run, result, outcome ) );

			return Task.CompletedTask;
		}
	}

	private sealed class DelegateDispatcher : IScheduledJobDispatcher
	{
		private readonly Func<ScheduledJobContext, CancellationToken, Task<JobOutcome>> _dispatch;

		public DelegateDispatcher( Func<ScheduledJobContext, CancellationToken, Task<JobOutcome>> dispatch )
		{
			_dispatch = dispatch;
		}

		public Task<JobOutcome> DispatchAsync( ScheduledJobContext context, CancellationToken cancellationToken ) =>
			_dispatch( context, cancellationToken );
	}

	private static ClaimedJob NewJob( TimeSpan? timeout = null ) =>
		new()
		{
			Context = new ScheduledJobContext
			{
				Schedule = "schedule-1",
				Run = "run-1",
				Domain = "tenant-1",
				Owner = new Reference { Type = "USER", Identifier = "owner-1" },
				JobType = "test",
				ScheduledFor = DateTime.UtcNow,
				Attempt = 0,
				Trigger = RunTriggers.Schedule
			},
			Timeout = timeout ?? TimeSpan.FromSeconds( 30 ),
			ClaimExpiry = DateTime.UtcNow.AddMinutes( 2 )
		};

	private static async Task<(string Run, string Result, JobOutcome Outcome)> RunWorkerAsync(
		FakeCoordinator coordinator, IScheduledJobDispatcher dispatcher )
	{
		SchedulerWorker worker = new(
				coordinator, dispatcher,
				new WorkerOptions { Claimant = "test-worker", PollInterval = TimeSpan.FromMilliseconds( 50 ) },
				NullLogger<SchedulerWorker>.Instance );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			return await coordinator.Reported.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ClaimedJobIsDispatchedAndSuccessReported()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateDispatcher dispatcher = new(
				( _, _ ) => Task.FromResult( new JobOutcome { Successful = true, Details = "{}" } ) );

		( string run, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, dispatcher );

		Assert.Equal( "run-1", run );
		Assert.Equal( RunResults.Succeeded, result );
		Assert.True( outcome.Successful );
	}

	[Fact]
	public async Task DispatcherExceptionIsReportedAsUnsuccessfulOutcome()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateDispatcher dispatcher = new(
				( _, _ ) => throw new InvalidOperationException( "handler exploded" ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, dispatcher );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
		Assert.Contains( "handler exploded", outcome.Error );
	}

	[Fact]
	public async Task NullOutcomeIsReportedAsUnsuccessful()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateDispatcher dispatcher = new( ( _, _ ) => Task.FromResult<JobOutcome>( null! ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, dispatcher );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
	}

	[Fact]
	public async Task TimeoutIsEnforcedAndClassified()
	{
		FakeCoordinator coordinator = new( NewJob( timeout: TimeSpan.FromMilliseconds( 100 ) ) );

		DelegateDispatcher dispatcher = new(
				async ( _, cancellationToken ) =>
				{
					await Task.Delay( TimeSpan.FromSeconds( 30 ), cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, dispatcher );

		Assert.Equal( RunResults.TimedOut, result );
		Assert.False( outcome.Successful );
	}
}
