using Microsoft.Extensions.Logging.Abstractions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker;

namespace Pug.Scheduling.Tests;

// Worker pump behavior against fake coordinator/handler registry: handler lookup, outcome classification,
// timeout enforcement, and defensiveness against third-party handler behavior.
public class SchedulerWorkerTests
{
	private sealed class FakeCoordinator : IExecutionCoordinator
	{
		private ClaimedJob? _job;

		public TaskCompletionSource<(string Run, string Result, JobOutcome Outcome)> Reported { get; } =
			new( TaskCreationOptions.RunContinuationsAsynchronously );

		// The caller identity of the claim that handed out the job.
		public (string Claimant, int Worker, string? JobType)? ClaimedBy { get; private set; }

		public (string Claimant, int Worker)? ReportedBy { get; private set; }

		// The caller identity of the latest renewal.
		public (string Claimant, int Worker)? RenewedBy { get; private set; }

		public FakeCoordinator( ClaimedJob job )
		{
			_job = job;
		}

		public Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType )
		{
			ClaimedJob? job = Interlocked.Exchange( ref _job, null );

			if( job is not null )
				ClaimedBy = ( claimant, worker, jobType );

			return Task.FromResult( job );
		}

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker )
		{
			RenewedBy = ( claimant, worker );

			return Task.FromResult( true );
		}

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			ReportedBy = ( claimant, worker );

			Reported.TrySetResult( ( run, result, outcome ) );

			return Task.CompletedTask;
		}
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
		FakeCoordinator coordinator, IScheduledJobHandlerFactoryRegistry registry, WorkerOptions? options = null,
		int number = 1 )
	{
		SchedulerWorker worker = new(
				coordinator, registry,
				options ?? new WorkerOptions { Claimant = "test-worker", PollInterval = TimeSpan.FromMilliseconds( 50 ) },
				number,
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

	private static SingleFactoryRegistry Registry( DelegateJobHandler handler ) =>
		SingleFactoryRegistry.For( "test", handler );

	[Fact]
	public async Task ClaimedJobIsExecutedAndSuccessReported()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateJobHandler handler = new(
				( _, _, _ ) => Task.FromResult( new JobOutcome { Successful = true, Details = "{}" } ) );

		( string run, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, Registry( handler ) );

		Assert.Equal( "run-1", run );
		Assert.Equal( RunResults.Succeeded, result );
		Assert.True( outcome.Successful );

		// A lone worker claims any job type as worker 1 of its claimant, and reports as the same.
		Assert.Equal( ( "test-worker", 1, (string?)null ), coordinator.ClaimedBy );
		Assert.Equal( ( "test-worker", 1 ), coordinator.ReportedBy );

		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task HandlerReceivesTheJobTimeout()
	{
		TimeSpan timeout = TimeSpan.FromSeconds( 17 );
		TimeSpan? received = null;

		FakeCoordinator coordinator = new( NewJob( timeout ) );

		DelegateJobHandler handler = new(
				( _, handlerTimeout, _ ) =>
				{
					received = handlerTimeout;

					return Task.FromResult( new JobOutcome { Successful = true } );
				} );

		await RunWorkerAsync( coordinator, Registry( handler ) );

		Assert.Equal( timeout, received );
	}

	[Fact]
	public async Task HandlerExceptionIsReportedAsUnsuccessfulOutcome()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateJobHandler handler = new(
				( _, _, _ ) => throw new InvalidOperationException( "handler exploded" ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, Registry( handler ) );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
		Assert.Contains( "handler exploded", outcome.Error );

		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task NullOutcomeIsReportedAsUnsuccessful()
	{
		FakeCoordinator coordinator = new( NewJob() );

		DelegateJobHandler handler = new( ( _, _, _ ) => Task.FromResult<JobOutcome>( null! ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, Registry( handler ) );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
	}

	[Fact]
	public async Task TimeoutIsEnforcedAndClassified()
	{
		FakeCoordinator coordinator = new( NewJob( timeout: TimeSpan.FromMilliseconds( 100 ) ) );

		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					await Task.Delay( TimeSpan.FromSeconds( 30 ), cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, Registry( handler ) );

		Assert.Equal( RunResults.TimedOut, result );
		Assert.False( outcome.Successful );

		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task UnknownJobTypeIsReportedAsFailed()
	{
		FakeCoordinator coordinator = new( NewJob() );

		SingleFactoryRegistry registry = new( "other", new DelegateJobHandlerFactory( _ => null ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, registry );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
		Assert.Equal( "No handler registered for job type 'test'.", outcome.Error );
	}

	[Fact]
	public async Task ThrowingFactoryIsReportedAsFailed()
	{
		FakeCoordinator coordinator = new( NewJob() );

		SingleFactoryRegistry registry = new(
				"test",
				new DelegateJobHandlerFactory( _ => throw new InvalidOperationException( "factory exploded" ) ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, registry );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
		Assert.Contains( "factory exploded", outcome.Error );
	}

	[Fact]
	public async Task FactoryReturningNoHandlerIsReportedAsFailed()
	{
		FakeCoordinator coordinator = new( NewJob() );

		SingleFactoryRegistry registry = new( "test", new DelegateJobHandlerFactory( _ => null ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync( coordinator, registry );

		Assert.Equal( RunResults.Failed, result );
		Assert.False( outcome.Successful );
		Assert.Equal( "Job handler factory returned no handler.", outcome.Error );
	}

	[Fact]
	public async Task WorkerClaimsRenewsAndReportsWithItsNumberAndJobType()
	{
		FakeCoordinator coordinator = new( NewJob() );

		// Holds the run long enough for a heartbeat.
		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					await Task.Delay( TimeSpan.FromMilliseconds( 300 ), cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		await RunWorkerAsync(
				coordinator, Registry( handler ),
				new WorkerOptions
				{
					Claimant = "report-workers",
					JobType = "test",
					PollInterval = TimeSpan.FromMilliseconds( 50 ),
					HeartbeatInterval = TimeSpan.FromMilliseconds( 50 )
				},
				number: 3 );

		Assert.Equal( ( "report-workers", 3, (string?)"test" ), coordinator.ClaimedBy );
		Assert.Equal( ( "report-workers", 3 ), coordinator.RenewedBy );
		Assert.Equal( ( "report-workers", 3 ), coordinator.ReportedBy );
	}

	[Fact]
	public void InfoReportsTheWorkersNumberAndJobType()
	{
		using SchedulerWorker worker = new(
				new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
				new WorkerOptions { Claimant = "report-workers", JobType = "report" },
				7,
				NullLogger<SchedulerWorker>.Instance );

		WorkerInfo info = worker.GetInfo();

		Assert.Equal( "report-workers", info.Claimant );
		Assert.Equal( 7, info.Worker );
		Assert.Equal( "report", info.JobType );
		Assert.Equal( 7, worker.Number );
		Assert.False( info.Paused );
		Assert.Null( info.LastPoll );
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( -1 )]
	public void NumberBelowOneIsRejected( int number )
	{
		ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
				() => new SchedulerWorker(
						new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
						new WorkerOptions(), number, NullLogger<SchedulerWorker>.Instance ) );

		Assert.Equal( "number", exception.ParamName );
	}

	[Fact]
	public void InvalidOptionsAreRejectedByTheWorker()
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(
				() => new SchedulerWorker(
						new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
						new WorkerOptions { Claimant = "group/1" }, 1, NullLogger<SchedulerWorker>.Instance ) );

		Assert.Equal( nameof(WorkerOptions.Claimant), exception.ParamName );
	}

	[Fact]
	public void DefaultOptionsClaimAnyTypeAsOneWorkerUnderTheMachineName()
	{
		WorkerOptions options = new();

		// Stable across restarts, so a restarted worker re-takes its own lease.
		Assert.Equal( Environment.MachineName, options.Claimant );
		Assert.Equal( 1, options.Workers );
		Assert.Null( options.JobType );

		options.Validate();
	}

	[Theory]
	[InlineData( null, 1, null, nameof(WorkerOptions.Claimant) )]
	[InlineData( "", 1, null, nameof(WorkerOptions.Claimant) )]
	[InlineData( "  ", 1, null, nameof(WorkerOptions.Claimant) )]
	[InlineData( "host/a", 1, null, nameof(WorkerOptions.Claimant) )]
	[InlineData( "host", 0, null, nameof(WorkerOptions.Workers) )]
	[InlineData( "host", -2, null, nameof(WorkerOptions.Workers) )]
	[InlineData( "host", 1, "", nameof(WorkerOptions.JobType) )]
	[InlineData( "host", 1, " ", nameof(WorkerOptions.JobType) )]
	public void InvalidOptionsAreRejected( string? claimant, int workers, string? jobType, string property )
	{
		WorkerOptions options = new() { Claimant = claimant!, Workers = workers, JobType = jobType! };

		ArgumentException exception = Assert.Throws<ArgumentException>( options.Validate );

		Assert.Equal( property, exception.ParamName );
	}

	[Theory]
	[InlineData( "host", 1, null )]
	[InlineData( "host-2.example", 4, "report" )]
	public void ValidOptionsAreAccepted( string claimant, int workers, string? jobType )
	{
		new WorkerOptions { Claimant = claimant, Workers = workers, JobType = jobType! }.Validate();
	}
}
