using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker;

namespace Pug.Scheduling.Tests;

// Worker pump behavior against fake coordinator/handler registry: handler lookup, outcome classification,
// timeout enforcement, detachment of handlers that ignore cancellation, and defensiveness against
// third-party handler behavior.
public class SchedulerWorkerTests
{
	private sealed class FakeCoordinator : IExecutionCoordinator
	{
		private readonly ConcurrentQueue<ClaimedJob> _jobs;

		private readonly ConcurrentDictionary<string, TaskCompletionSource<(string Result, JobOutcome Outcome)>>
			_reports = new();

		private int _claims;
		private TaskCompletionSource _claimed = Signal();

		// The first report, whichever run it is for.
		public TaskCompletionSource<(string Run, string Result, JobOutcome Outcome)> Reported { get; } =
			new( TaskCreationOptions.RunContinuationsAsynchronously );

		// The caller identity of the claim that handed out the job.
		public (string Claimant, int Worker, string? JobType)? ClaimedBy { get; private set; }

		public (string Claimant, int Worker)? ReportedBy { get; private set; }

		// The caller identity of the latest renewal.
		public (string Claimant, int Worker)? RenewedBy { get; private set; }

		// What every renewal answers; false tells the worker its claim is lost.
		public bool Renews { get; init; } = true;

		// Claim attempts so far, including those that found no job.
		public int Claims => Volatile.Read( ref _claims );

		public int Reports => _reports.Values.Count( report => report.Task.IsCompleted );

		public FakeCoordinator( params ClaimedJob[] jobs )
		{
			_jobs = new ConcurrentQueue<ClaimedJob>( jobs );
		}

		// Completes with the run's report.
		public Task<(string Result, JobOutcome Outcome)> ReportOf( string run ) => ReportSignal( run ).Task;

		private TaskCompletionSource<(string Result, JobOutcome Outcome)> ReportSignal( string run ) =>
			_reports.GetOrAdd( run, _ => new( TaskCreationOptions.RunContinuationsAsynchronously ) );

		// Completes once the worker has attempted at least the given number of claims.
		public async Task ClaimsReachedAsync( int claims )
		{
			while( true )
			{
				Task claimed = _claimed.Task;

				if( Claims >= claims )
					return;

				await claimed;
			}
		}

		public Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType )
		{
			ClaimedJob? job = _jobs.TryDequeue( out ClaimedJob? next ) ? next : null;

			if( job is not null )
				ClaimedBy = ( claimant, worker, jobType );

			Interlocked.Increment( ref _claims );
			Interlocked.Exchange( ref _claimed, Signal() ).TrySetResult();

			return Task.FromResult( job );
		}

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker )
		{
			RenewedBy = ( claimant, worker );

			return Task.FromResult( Renews );
		}

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			ReportedBy = ( claimant, worker );

			Reported.TrySetResult( ( run, result, outcome ) );
			ReportSignal( run ).TrySetResult( ( result, outcome ) );

			return Task.CompletedTask;
		}
	}

	// Handlers by run, for jobs that must behave differently.
	private sealed class PerRunRegistry( Dictionary<string, DelegateJobHandler> handlers )
		: IScheduledJobHandlerFactoryRegistry
	{
		public IScheduledJobHandlerFactory GetFactory( string jobType ) =>
			new DelegateJobHandlerFactory( context => handlers[context.Run] );
	}

	// Generous: every wait below ends on a signal well within it unless the worker is broken.
	private static readonly TimeSpan Bound = TimeSpan.FromSeconds( 10 );

	private static TaskCompletionSource Signal() => new( TaskCreationOptions.RunContinuationsAsynchronously );

	private static ClaimedJob NewJob( TimeSpan? timeout = null, string run = "run-1" ) =>
		new()
		{
			Context = new ScheduledJobContext
			{
				Schedule = "schedule-1",
				Run = run,
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

	private static WorkerOptions FastOptions( int maxDetachedHandlers = 2, int graceMilliseconds = 100 ) =>
		new()
		{
			Claimant = "test-worker",
			PollInterval = TimeSpan.FromMilliseconds( 20 ),
			HeartbeatInterval = TimeSpan.FromMilliseconds( 20 ),
			CancellationGrace = TimeSpan.FromMilliseconds( graceMilliseconds ),
			MaxDetachedHandlers = maxDetachedHandlers
		};

	private static SchedulerWorker NewWorker( FakeCoordinator coordinator, IScheduledJobHandlerFactoryRegistry registry,
											WorkerOptions? options = null, int number = 1 ) =>
		new( coordinator, registry,
			options ?? new WorkerOptions { Claimant = "test-worker", PollInterval = TimeSpan.FromMilliseconds( 50 ) },
			number,
			TimeProvider.System,
			NullLogger<SchedulerWorker>.Instance );

	private static async Task<(string Run, string Result, JobOutcome Outcome)> RunWorkerAsync(
		FakeCoordinator coordinator, IScheduledJobHandlerFactoryRegistry registry, WorkerOptions? options = null,
		int number = 1 )
	{
		SchedulerWorker worker = NewWorker( coordinator, registry, options, number );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			return await coordinator.Reported.Task.WaitAsync( Bound );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	// Polls a condition that has no signal of its own, within the bound.
	private static async Task WaitUntilAsync( Func<bool> condition )
	{
		using CancellationTokenSource bound = new( Bound );

		while( !condition() )
			await Task.Delay( TimeSpan.FromMilliseconds( 10 ), bound.Token );
	}

	private static SingleFactoryRegistry Registry( DelegateJobHandler handler ) =>
		SingleFactoryRegistry.For( "test", handler );

	// A handler that ignores cancellation and ends only when released.
	private static DelegateJobHandler Hanging( TaskCompletionSource release ) =>
		new( async ( _, _, _ ) =>
		{
			await release.Task;

			return new JobOutcome { Successful = true };
		} );

	private static DelegateJobHandler Succeeding() =>
		new( ( _, _, _ ) => Task.FromResult( new JobOutcome { Successful = true } ) );

	// A handler that honours cancellation, signalling when it starts and when it is cancelled.
	private static DelegateJobHandler Cooperative( TaskCompletionSource started, TaskCompletionSource cancelled ) =>
		new( async ( _, _, cancellationToken ) =>
		{
			started.TrySetResult();

			try
			{
				await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
			}
			finally
			{
				cancelled.TrySetResult();
			}

			return new JobOutcome { Successful = true };
		} );

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
		Assert.StartsWith( "Execution exceeded the schedule timeout", outcome.Error );

		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task HangingHandlerIsDetachedAndWorkerContinues()
	{
		TaskCompletionSource release = Signal();

		DelegateJobHandler hanging = Hanging( release );
		DelegateJobHandler next = Succeeding();

		FakeCoordinator coordinator = new(
				NewJob( timeout: TimeSpan.FromMilliseconds( 100 ), run: "hanging" ), NewJob( run: "next" ) );

		SchedulerWorker worker = NewWorker(
				coordinator, new PerRunRegistry( new() { ["hanging"] = hanging, ["next"] = next } ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, JobOutcome outcome ) = await coordinator.ReportOf( "hanging" ).WaitAsync( Bound );

			Assert.Equal( RunResults.TimedOut, result );
			Assert.Equal( "Handler did not stop within 00:00:00.1000000 of cancellation; detached.", outcome.Error );

			// The worker moved on while the detached handler still runs, undisposed.
			( string nextResult, _ ) = await coordinator.ReportOf( "next" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, nextResult );
			Assert.Equal( 1, worker.GetInfo().DetachedJobs );
			Assert.Equal( 0, hanging.Disposals );

			// When it finally ends, it is disposed and no longer counted.
			release.TrySetResult();

			await WaitUntilAsync( () => worker.GetInfo().DetachedJobs == 0 );

			Assert.Equal( 1, hanging.Disposals );
			Assert.Equal( 2, coordinator.Reports );
		}
		finally
		{
			release.TrySetResult();

			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task DetachedBoundPausesClaiming()
	{
		TaskCompletionSource release = Signal();

		DelegateJobHandler hanging = Hanging( release );

		FakeCoordinator coordinator = new(
				NewJob( timeout: TimeSpan.FromMilliseconds( 100 ), run: "hanging" ), NewJob( run: "next" ) );

		SchedulerWorker worker = NewWorker(
				coordinator, new PerRunRegistry( new() { ["hanging"] = hanging, ["next"] = Succeeding() } ),
				FastOptions( maxDetachedHandlers: 1 ) );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ReportOf( "hanging" ).WaitAsync( Bound );

			// At the bound: many poll intervals pass without another claim.
			await Task.Delay( TimeSpan.FromMilliseconds( 200 ) );

			Assert.Equal( 1, coordinator.Claims );
			Assert.Equal( 1, worker.GetInfo().DetachedJobs );
			Assert.False( coordinator.ReportOf( "next" ).IsCompleted );

			// The detached handler ending frees the worker to claim again.
			release.TrySetResult();

			( string result, _ ) = await coordinator.ReportOf( "next" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, result );
			Assert.Equal( 1, hanging.Disposals );
		}
		finally
		{
			release.TrySetResult();

			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task LostClaimAbandonsWithoutReport()
	{
		TaskCompletionSource started = Signal();
		TaskCompletionSource cancelled = Signal();

		DelegateJobHandler handler = Cooperative( started, cancelled );

		FakeCoordinator coordinator = new( NewJob() ) { Renews = false };

		// A generous grace: it is never waited for, the handler honours cancellation.
		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await cancelled.Task.WaitAsync( Bound );

			// The next claim attempt follows only once the abandoned run is fully processed.
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.Equal( 0, coordinator.Reports );
			Assert.Equal( 1, handler.Disposals );
			Assert.Equal( 0, worker.GetInfo().DetachedJobs );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ShutdownAbandonsWithoutReport()
	{
		TaskCompletionSource started = Signal();
		TaskCompletionSource cancelled = Signal();

		DelegateJobHandler handler = Cooperative( started, cancelled );

		FakeCoordinator coordinator = new( NewJob() );

		// A generous grace: it is never waited for, the handler honours cancellation.
		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		await started.Task.WaitAsync( Bound );

		// Returns once the worker's loop has ended, so the run is fully processed.
		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.True( cancelled.Task.IsCompleted );
		Assert.Equal( 0, coordinator.Reports );
		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task DetachedHandlerOfALostClaimIsNotReported()
	{
		TaskCompletionSource release = Signal();

		DelegateJobHandler hanging = Hanging( release );

		FakeCoordinator coordinator = new( NewJob() ) { Renews = false };

		SchedulerWorker worker = NewWorker( coordinator, Registry( hanging ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await WaitUntilAsync( () => worker.GetInfo().DetachedJobs == 1 );

			// The worker moved on (it keeps polling) without reporting.
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.Equal( 0, coordinator.Reports );
			Assert.Equal( 1, worker.GetInfo().DetachedJobs );
			Assert.Equal( 0, worker.GetInfo().ActiveJobs );
		}
		finally
		{
			release.TrySetResult();

			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ShutdownDetachesAHandlerThatIgnoresCancellationAndStopReturns()
	{
		TaskCompletionSource started = Signal();
		TaskCompletionSource release = Signal();

		DelegateJobHandler hanging = new(
				async ( _, _, _ ) =>
				{
					started.TrySetResult();

					await release.Task;

					return new JobOutcome { Successful = true };
				} );

		FakeCoordinator coordinator = new( NewJob() );

		SchedulerWorker worker = NewWorker( coordinator, Registry( hanging ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await started.Task.WaitAsync( Bound );

			// Returns after the grace, not after the handler.
			await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

			Assert.Equal( 0, coordinator.Reports );
			Assert.Equal( 1, worker.GetInfo().DetachedJobs );
			Assert.Equal( 0, hanging.Disposals );
		}
		finally
		{
			release.TrySetResult();
		}
	}

	[Fact]
	public async Task DetachedHandlerThatEndsByThrowingIsStillReleased()
	{
		TaskCompletionSource release = Signal();

		DelegateJobHandler hanging = new(
				async ( _, _, _ ) =>
				{
					await release.Task;

					throw new InvalidOperationException( "late failure" );
				} );

		FakeCoordinator coordinator = new( NewJob( timeout: TimeSpan.FromMilliseconds( 100 ) ) );

		SchedulerWorker worker = NewWorker( coordinator, Registry( hanging ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ReportOf( "run-1" ).WaitAsync( Bound );

			Assert.Equal( 1, worker.GetInfo().DetachedJobs );

			release.TrySetResult();

			await WaitUntilAsync( () => worker.GetInfo().DetachedJobs == 0 );

			Assert.Equal( 1, hanging.Disposals );
			Assert.Equal( 1, coordinator.Reports );
		}
		finally
		{
			release.TrySetResult();

			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task HandlerFinishingAfterTheClaimWasLostIsNotReported()
	{
		TaskCompletionSource cancelled = Signal();

		// Notices the cancellation but ends normally with an outcome, within the grace.
		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					try
					{
						await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
					}
					catch( OperationCanceledException )
					{
						cancelled.TrySetResult();
					}

					return new JobOutcome { Successful = true };
				} );

		FakeCoordinator coordinator = new( NewJob() ) { Renews = false };

		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await cancelled.Task.WaitAsync( Bound );

			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.Equal( 0, coordinator.Reports );
			Assert.Equal( 1, handler.Disposals );
			Assert.Equal( 0, worker.GetInfo().DetachedJobs );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ThrowingCancellationCallbackDoesNotFaultTheWorker()
	{
		TaskCompletionSource cancelled = Signal();

		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					using CancellationTokenRegistration registration =
						cancellationToken.Register( () => throw new InvalidOperationException( "callback exploded" ) );

					try
					{
						await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );
					}
					finally
					{
						cancelled.TrySetResult();
					}

					return new JobOutcome { Successful = true };
				} );

		FakeCoordinator coordinator = new( NewJob() ) { Renews = false };

		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await cancelled.Task.WaitAsync( Bound );

			// The pump survives: the abandoned run is processed and polling goes on.
			await coordinator.ClaimsReachedAsync( 3 ).WaitAsync( Bound );

			Assert.Equal( 0, coordinator.Reports );
			Assert.Equal( 1, handler.Disposals );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ThrowingCancellationCallbackOnTimeoutStillReportsTimedOut()
	{
		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					using CancellationTokenRegistration registration =
						cancellationToken.Register( () => throw new InvalidOperationException( "callback exploded" ) );

					await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		FakeCoordinator coordinator = new( NewJob( timeout: TimeSpan.FromMilliseconds( 100 ) ) );

		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, _ ) = await coordinator.ReportOf( "run-1" ).WaitAsync( Bound );

			Assert.Equal( RunResults.TimedOut, result );

			// The worker keeps polling.
			await coordinator.ClaimsReachedAsync( coordinator.Claims + 2 ).WaitAsync( Bound );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ThrowingCancellationCallbackOnShutdownDoesNotFaultStop()
	{
		TaskCompletionSource started = Signal();

		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					using CancellationTokenRegistration registration =
						cancellationToken.Register( () => throw new InvalidOperationException( "callback exploded" ) );

					started.TrySetResult();

					await Task.Delay( Timeout.InfiniteTimeSpan, cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		FakeCoordinator coordinator = new( NewJob() );

		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		await started.Task.WaitAsync( Bound );

		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.Equal( 0, coordinator.Reports );
		Assert.Equal( 1, handler.Disposals );
	}

	[Fact]
	public async Task JobWithOversizedTimeoutIsFailedAndWorkerContinues()
	{
		DelegateJobHandler never = Succeeding();

		FakeCoordinator coordinator = new(
				NewJob( timeout: TimerBounds.Maximum + TimeSpan.FromMilliseconds( 1 ), run: "huge" ),
				NewJob( run: "next" ) );

		SchedulerWorker worker = NewWorker(
				coordinator, new PerRunRegistry( new() { ["huge"] = never, ["next"] = Succeeding() } ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, JobOutcome outcome ) = await coordinator.ReportOf( "huge" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Failed, result );
			Assert.False( outcome.Successful );
			Assert.Contains( "outside the supported range", outcome.Error );

			( string nextResult, _ ) = await coordinator.ReportOf( "next" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, nextResult );
			Assert.Equal( 0, never.Disposals );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
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
		using SchedulerWorker worker = NewWorker(
				new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
				new WorkerOptions { Claimant = "report-workers", JobType = "report" },
				7 );

		WorkerInfo info = worker.GetInfo();

		Assert.Equal( "report-workers", info.Claimant );
		Assert.Equal( 7, info.Worker );
		Assert.Equal( "report", info.JobType );
		Assert.Equal( 7, worker.Number );
		Assert.False( info.Paused );
		Assert.Null( info.LastPoll );
		Assert.Equal( 0, info.ActiveJobs );
		Assert.Equal( 0, info.DetachedJobs );
	}

	[Fact]
	public async Task LastPollIsTakenFromTheInjectedClock()
	{
		DateTime now = new( 2030, 1, 2, 3, 4, 5, DateTimeKind.Utc );

		FakeCoordinator coordinator = new();

		SchedulerWorker worker = new(
				coordinator, Registry( Succeeding() ), FastOptions(), 1, new ManualTimeProvider( now ),
				NullLogger<SchedulerWorker>.Instance );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ClaimsReachedAsync( 1 ).WaitAsync( Bound );

			await WaitUntilAsync( () => worker.GetInfo().LastPoll is not null );

			Assert.Equal( now, worker.GetInfo().LastPoll );
			Assert.Equal( DateTimeKind.Utc, worker.GetInfo().LastPoll!.Value.Kind );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( -1 )]
	public void NumberBelowOneIsRejected( int number )
	{
		ArgumentOutOfRangeException exception = Assert.Throws<ArgumentOutOfRangeException>(
				() => NewWorker(
						new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
						new WorkerOptions(), number ) );

		Assert.Equal( "number", exception.ParamName );
	}

	[Fact]
	public void InvalidOptionsAreRejectedByTheWorker()
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(
				() => NewWorker(
						new FakeCoordinator( NewJob() ), Registry( new DelegateJobHandler( ( _, _, _ ) => null! ) ),
						new WorkerOptions { Claimant = "group/1" }, 1 ) );

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
		Assert.Equal( TimeSpan.FromSeconds( 10 ), options.CancellationGrace );
		Assert.Equal( 2, options.MaxDetachedHandlers );

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
	[InlineData( -1, 2, nameof(WorkerOptions.CancellationGrace) )]
	[InlineData( 0, 0, nameof(WorkerOptions.MaxDetachedHandlers) )]
	[InlineData( 0, -1, nameof(WorkerOptions.MaxDetachedHandlers) )]
	public void InvalidDetachOptionsAreRejected( int graceMilliseconds, int maxDetachedHandlers, string property )
	{
		WorkerOptions options = new()
		{
			Claimant = "host",
			CancellationGrace = TimeSpan.FromMilliseconds( graceMilliseconds ),
			MaxDetachedHandlers = maxDetachedHandlers
		};

		ArgumentException exception = Assert.Throws<ArgumentException>( options.Validate );

		Assert.Equal( property, exception.ParamName );
	}

	private static WorkerOptions WithDuration( string property, TimeSpan value ) =>
		property switch
		{
			nameof(WorkerOptions.PollInterval) => new WorkerOptions { Claimant = "host", PollInterval = value },
			nameof(WorkerOptions.HeartbeatInterval) => new WorkerOptions { Claimant = "host", HeartbeatInterval = value },
			nameof(WorkerOptions.CancellationGrace) => new WorkerOptions { Claimant = "host", CancellationGrace = value },
			_ => throw new ArgumentOutOfRangeException( nameof(property) )
		};

	[Theory]
	[InlineData( nameof(WorkerOptions.PollInterval), 0 )]
	[InlineData( nameof(WorkerOptions.PollInterval), -1 )]
	[InlineData( nameof(WorkerOptions.HeartbeatInterval), 0 )]
	[InlineData( nameof(WorkerOptions.HeartbeatInterval), -1 )]
	public void NonPositiveIntervalsAreRejected( string property, int milliseconds )
	{
		ArgumentException exception = Assert.Throws<ArgumentException>(
				WithDuration( property, TimeSpan.FromMilliseconds( milliseconds ) ).Validate );

		Assert.Equal( property, exception.ParamName );
	}

	[Theory]
	[InlineData( nameof(WorkerOptions.PollInterval) )]
	[InlineData( nameof(WorkerOptions.HeartbeatInterval) )]
	[InlineData( nameof(WorkerOptions.CancellationGrace) )]
	public void DurationsBeyondTheTimerBoundAreRejectedAndTheBoundIsAccepted( string property )
	{
		WithDuration( property, TimerBounds.Maximum ).Validate();

		ArgumentException exception = Assert.Throws<ArgumentException>(
				WithDuration( property, TimerBounds.Maximum + TimeSpan.FromMilliseconds( 1 ) ).Validate );

		Assert.Equal( property, exception.ParamName );
	}

	[Theory]
	[InlineData( "host", 1, null )]
	[InlineData( "host-2.example", 4, "report" )]
	public void ValidOptionsAreAccepted( string claimant, int workers, string? jobType )
	{
		new WorkerOptions { Claimant = claimant, Workers = workers, JobType = jobType! }.Validate();
	}

	[Fact]
	public void ZeroGraceAndOneDetachedHandlerAreAccepted()
	{
		new WorkerOptions { Claimant = "host", CancellationGrace = TimeSpan.Zero, MaxDetachedHandlers = 1 }.Validate();
	}
}
