using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Authentication;
using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Security;
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

		// What every renewal answers; false tells the worker its claim is lost. Ignored when
		// OnRenew is set.
		public bool Renews { get; init; } = true;

		// Answers the n-th renewal (from 1): its result, or a failure it throws or faults with.
		public Func<int, Task<bool>>? OnRenew { get; init; }

		// Runs before the n-th report attempt (from 1) is recorded: completing records it, throwing
		// or faulting fails the attempt, which is then not recorded.
		public Func<int, Task>? OnReport { get; set; }

		// Runs before every claim; throwing fails the claim.
		public Action? OnClaim { get; init; }

		// Hands the job last claimed back on every claim until its report is recorded, as the real
		// coordinator adopts its holder's own unfinished run (D20).
		public bool ReOffer { get; init; }

		private ClaimedJob? _offered;
		private int _renewals;
		private int _reportAttempts;

		public int Renewals => Volatile.Read( ref _renewals );

		public int ReportAttempts => Volatile.Read( ref _reportAttempts );

		// Claim attempts made by the time of each report attempt, in order.
		public ConcurrentQueue<int> ClaimsAtReportAttempts { get; } = new();

		// Stopwatch timestamps of every claim attempt and every report attempt, in order.
		public ConcurrentQueue<long> ClaimTimes { get; } = new();

		public ConcurrentQueue<long> ReportTimes { get; } = new();

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
			ClaimTimes.Enqueue( Stopwatch.GetTimestamp() );

			try
			{
				OnClaim?.Invoke();

				ClaimedJob? offered = Volatile.Read( ref _offered );

				ClaimedJob? job = ReOffer && offered is not null && !ReportOf( offered.Context.Run ).IsCompleted
									? offered
									: _jobs.TryDequeue( out ClaimedJob? next ) ? next : null;

				if( job is not null )
				{
					ClaimedBy = ( claimant, worker, jobType );
					Volatile.Write( ref _offered, job );
				}

				return Task.FromResult( job );
			}
			finally
			{
				Interlocked.Increment( ref _claims );
				Interlocked.Exchange( ref _claimed, Signal() ).TrySetResult();
			}
		}

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker )
		{
			RenewedBy = ( claimant, worker );

			int renewal = Interlocked.Increment( ref _renewals );

			return OnRenew is null ? Task.FromResult( Renews ) : OnRenew( renewal );
		}

		// Runs the worker asked to abandon, in order.
		public ConcurrentQueue<(string Run, string Claimant, int Worker)> Abandoned { get; } = new();

		// What every abandon does; defaults to releasing the claim.
		public Func<Task<bool>> OnAbandon { get; init; } = () => Task.FromResult( true );

		public Task<bool> AbandonClaimAsync( string run, string claimant, int worker )
		{
			Abandoned.Enqueue( ( run, claimant, worker ) );

			return OnAbandon();
		}

		public async Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			ClaimsAtReportAttempts.Enqueue( Claims );
			ReportTimes.Enqueue( Stopwatch.GetTimestamp() );

			int attempt = Interlocked.Increment( ref _reportAttempts );

			if( OnReport is not null )
				await OnReport( attempt );

			ReportedBy = ( claimant, worker );

			Reported.TrySetResult( ( run, result, outcome ) );
			ReportSignal( run ).TrySetResult( ( result, outcome ) );
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

	private static ClaimedJob NewJob( TimeSpan? timeout = null, string run = "run-1", TimeSpan? claimDuration = null ) =>
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
			ClaimExpiry = DateTime.UtcNow.AddMinutes( 2 ),
			ClaimDuration = claimDuration ?? TimeSpan.FromMinutes( 2 )
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
	public async Task ShutdownWithACooperativeHandlerReleasesTheClaimOnce()
	{
		TaskCompletionSource started = Signal();

		FakeCoordinator coordinator = new( NewJob() );

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Cooperative( started, Signal() ) ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		await started.Task.WaitAsync( Bound );

		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.Equal( [ ( "run-1", "test-worker", 1 ) ], coordinator.Abandoned.ToArray() );
		Assert.Equal( 0, coordinator.Reports );
	}

	[Fact]
	public async Task ShutdownWithADetachedHandlerDoesNotReleaseTheClaim()
	{
		TaskCompletionSource started = Signal();
		TaskCompletionSource release = Signal();

		FakeCoordinator coordinator = new( NewJob() );

		DelegateJobHandler hanging = new(
				async ( _, _, _ ) =>
				{
					started.TrySetResult();

					await release.Task;

					return new JobOutcome { Successful = true };
				} );

		SchedulerWorker worker = NewWorker( coordinator, Registry( hanging ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await started.Task.WaitAsync( Bound );

			await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

			Assert.Equal( 1, worker.GetInfo().DetachedJobs );
			Assert.Empty( coordinator.Abandoned );
		}
		finally
		{
			release.TrySetResult();
		}
	}

	[Fact]
	public async Task ALostClaimIsNotReleased()
	{
		TaskCompletionSource started = Signal();
		TaskCompletionSource cancelled = Signal();

		FakeCoordinator coordinator = new( NewJob() ) { Renews = false };

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Cooperative( started, cancelled ) ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await cancelled.Task.WaitAsync( Bound );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );
		}

		Assert.Empty( coordinator.Abandoned );
		Assert.Equal( 0, coordinator.Reports );
	}

	[Fact]
	public async Task AFailingReleaseDoesNotFaultShutdown()
	{
		TaskCompletionSource started = Signal();

		FakeCoordinator coordinator = new( NewJob() )
		{
			OnAbandon = () => Task.FromException<bool>( new InvalidOperationException( "unreachable" ) )
		};

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Cooperative( started, Signal() ) ), FastOptions( graceMilliseconds: 10_000 ) );

		await worker.StartAsync( CancellationToken.None );

		await started.Task.WaitAsync( Bound );

		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.Single( coordinator.Abandoned );
	}

	[Fact]
	public async Task AHangingReleaseIsGivenUpOnAfterTheAbandonTimeout()
	{
		TaskCompletionSource started = Signal();

		FakeCoordinator coordinator = new( NewJob() ) { OnAbandon = () => new TaskCompletionSource<bool>().Task };

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Cooperative( started, Signal() ) ),
				FastOptions( graceMilliseconds: 10_000 ) with { AbandonTimeout = TimeSpan.FromMilliseconds( 100 ) } );

		await worker.StartAsync( CancellationToken.None );

		await started.Task.WaitAsync( Bound );

		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.Single( coordinator.Abandoned );
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
		Assert.Equal( TimeSpan.FromSeconds( 5 ), options.AbandonTimeout );
		Assert.Equal( TimeSpan.FromMinutes( 1 ), options.RefusedPollRetryInterval );

		options.Validate();
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( -1 )]
	public void NonPositiveAbandonTimeoutIsRejected( int milliseconds )
	{
		WorkerOptions options = new() { Claimant = "host", AbandonTimeout = TimeSpan.FromMilliseconds( milliseconds ) };

		ArgumentException exception = Assert.Throws<ArgumentException>( options.Validate );

		Assert.Equal( nameof(WorkerOptions.AbandonTimeout), exception.ParamName );
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
			nameof(WorkerOptions.RefusedPollRetryInterval) =>
				new WorkerOptions { Claimant = "host", RefusedPollRetryInterval = value },
			_ => throw new ArgumentOutOfRangeException( nameof(property) )
		};

	[Theory]
	[InlineData( nameof(WorkerOptions.PollInterval), 0 )]
	[InlineData( nameof(WorkerOptions.PollInterval), -1 )]
	[InlineData( nameof(WorkerOptions.HeartbeatInterval), 0 )]
	[InlineData( nameof(WorkerOptions.HeartbeatInterval), -1 )]
	[InlineData( nameof(WorkerOptions.RefusedPollRetryInterval), 0 )]
	[InlineData( nameof(WorkerOptions.RefusedPollRetryInterval), -1 )]
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
	[InlineData( nameof(WorkerOptions.RefusedPollRetryInterval) )]
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

	// ===== The claim's lifecycle (D29) =====

	// The shortest claim the worker accepts (MinimumClaimDuration): a 0.5 s window after
	// LeaseTolerance.
	private static readonly TimeSpan ShortClaim = TimeSpan.FromSeconds( 1 );

	// A handler that succeeds after the given time, counting its executions.
	private static DelegateJobHandler Counting( Func<int> onStart, TimeSpan? duration = null ) =>
		new( async ( _, _, cancellationToken ) =>
		{
			onStart();

			if( duration is not null )
				await Task.Delay( duration.Value, cancellationToken );

			return new JobOutcome { Successful = true };
		} );

	// A coordinator failure of each kind the worker retries: a refusal is one of them (D29).
	private static Exception Failure( string failure ) =>
		failure switch
		{
			"refused" => new NotAuthorized(),
			"unauthenticated" => new AuthenticationException(),
			"network" => new HttpRequestException( "unreachable" ),
			_ => new ArgumentException( "bad stored data", "run" )
		};

	[Theory]
	[InlineData( "refused" )]
	[InlineData( "unauthenticated" )]
	[InlineData( "network" )]
	[InlineData( "argument" )]
	public async Task RenewalsFailingForTheWholeLeaseLoseTheClaimWithoutPausing( string failure )
	{
		Exception exception = Failure( failure );

		TaskCompletionSource cancelled = Signal();

		FakeCoordinator coordinator = new( NewJob( claimDuration: ShortClaim ) )
		{
			OnRenew = _ => Task.FromException<bool>( exception )
		};

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Cooperative( Signal(), cancelled ) ), FastOptions( graceMilliseconds: 10_000 ) );

		Stopwatch elapsed = Stopwatch.StartNew();

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await cancelled.Task.WaitAsync( Bound );

			// Not before the worker's deadline: ClaimDuration less the tolerance, from the claim.
			Assert.True( elapsed.Elapsed >= ShortClaim - SchedulerWorker.LeaseTolerance - TimeSpan.FromMilliseconds( 50 ),
						$"Claim lost after {elapsed.Elapsed}." );

			// The pump moves on; a lapsed claim is neither reported, released nor a fault.
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.True( coordinator.Renewals > 1 );
			Assert.Equal( 0, coordinator.ReportAttempts );
			Assert.Empty( coordinator.Abandoned );
			Assert.False( worker.GetInfo().Paused );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Theory]
	[InlineData( "refused" )]
	[InlineData( "network" )]
	public async Task RenewalsFailingAndThenConfirmedKeepTheRun( string failure )
	{
		TaskCompletionSource renewedAfterFailures = Signal();

		// Three failures within the window (the 20 ms beat), then a confirmed renewal.
		FakeCoordinator coordinator = new( NewJob( claimDuration: TimeSpan.FromSeconds( 3 ) ) )
		{
			OnRenew = renewal =>
			{
				if( renewal <= 3 )
					return Task.FromException<bool>( Failure( failure ) );

				renewedAfterFailures.TrySetResult();

				return Task.FromResult( true );
			}
		};

		// Ends once the claim has come through the failures, so its outcome is reported under it.
		DelegateJobHandler handler = new(
				async ( _, _, cancellationToken ) =>
				{
					await renewedAfterFailures.Task.WaitAsync( cancellationToken );

					return new JobOutcome { Successful = true };
				} );

		SchedulerWorker worker = NewWorker( coordinator, Registry( handler ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, _ ) = await coordinator.ReportOf( "run-1" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, result );
			Assert.True( coordinator.Renewals > 3 );
			Assert.Empty( coordinator.Abandoned );
			Assert.False( worker.GetInfo().Paused );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task AHangingRenewalLosesTheClaimAtTheDeadlineAndDoesNotDelayTheDetach()
	{
		TaskCompletionSource release = Signal();

		FakeCoordinator coordinator = new( NewJob( claimDuration: ShortClaim ) )
		{
			OnRenew = _ => new TaskCompletionSource<bool>().Task
		};

		SchedulerWorker worker = NewWorker( coordinator, Registry( Hanging( release ) ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			// The handler ignores cancellation, so it is detached after the grace; the heartbeat,
			// stuck in its first renewal, must neither keep the run going nor hold up the detach.
			await WaitUntilAsync( () => worker.GetInfo().DetachedJobs == 1 );

			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.Equal( 1, coordinator.Renewals );
			Assert.Equal( 0, coordinator.ReportAttempts );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			release.TrySetResult();

			// Bounded: a heartbeat stuck in the renewal would otherwise hang the test run.
			await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );
		}
	}

	[Fact]
	public async Task SuccessfulRenewalsCarryARunPastSeveralLeases()
	{
		FakeCoordinator coordinator = new( NewJob( claimDuration: TimeSpan.FromSeconds( 2 ) ) );

		// Over two and a half times the worker's 1.5 s window for the claim.
		( _, string result, _ ) = await RunWorkerAsync(
				coordinator, Registry( Counting( () => 0, TimeSpan.FromSeconds( 4 ) ) ), FastOptions() );

		Assert.Equal( RunResults.Succeeded, result );
	}

	[Fact]
	public async Task TheBeatIsCappedAtAThirdOfTheWorkersWindow()
	{
		// A 2.5 s window: a beat of about 0.83 s, where half the window would be 1.25 s.
		FakeCoordinator coordinator = new( NewJob( claimDuration: TimeSpan.FromSeconds( 3 ) ) );

		// A HeartbeatInterval far beyond the lease would never renew in time uncapped. Over 3.5 s,
		// a third-of-window beat renews four times, a half-window one twice.
		( _, string result, _ ) = await RunWorkerAsync(
				coordinator, Registry( Counting( () => 0, TimeSpan.FromSeconds( 3.5 ) ) ),
				FastOptions() with { HeartbeatInterval = TimeSpan.FromMinutes( 1 ) } );

		Assert.Equal( RunResults.Succeeded, result );
		Assert.True( coordinator.Renewals >= 3, $"{coordinator.Renewals} renewal(s)." );
	}

	[Theory]
	[InlineData( 0 )]
	[InlineData( 500 )]
	[InlineData( 999 )]
	[InlineData( -1 )]
	public async Task AClaimDurationTheWorkerCannotTimeFailsTheRunUnexecuted( int milliseconds )
	{
		int executions = 0;

		FakeCoordinator coordinator = new( NewJob( claimDuration: TimeSpan.FromMilliseconds( milliseconds ) ) );

		( _, string result, JobOutcome outcome ) = await RunWorkerAsync(
				coordinator, Registry( Counting( () => Interlocked.Increment( ref executions ) ) ), FastOptions() );

		Assert.Equal( RunResults.Failed, result );
		Assert.Contains( "outside the range the worker can time", outcome.Error );
		Assert.Equal( 0, executions );
		Assert.Equal( 1, coordinator.ReportAttempts );
	}

	[Fact]
	public async Task AClaimDurationBeyondTheTimerBoundFailsTheRunUnexecuted()
	{
		int executions = 0;

		FakeCoordinator coordinator = new(
				NewJob( claimDuration: TimerBounds.Maximum + TimeSpan.FromMilliseconds( 1 ) ) );

		( _, string result, _ ) = await RunWorkerAsync(
				coordinator, Registry( Counting( () => Interlocked.Increment( ref executions ) ) ), FastOptions() );

		Assert.Equal( RunResults.Failed, result );
		Assert.Equal( 0, executions );
	}

	[Theory]
	[InlineData( "claim not held" )]
	[InlineData( "unknown run" )]
	public async Task ACompletionForAClaimNoLongerHeldIsNotRetried( string failure )
	{
		FakeCoordinator coordinator = new( NewJob() )
		{
			OnReport = _ => throw ( failure == "unknown run"
										? new UnknownRunException( "run-1" )
										: new ClaimNotHeldException( "run-1" ) )
		};

		SchedulerWorker worker = NewWorker( coordinator, Registry( Succeeding() ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ClaimsReachedAsync( 3 ).WaitAsync( Bound );

			Assert.Equal( 1, coordinator.ReportAttempts );
			Assert.Empty( coordinator.Abandoned );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task StoppingTheHostEndsCompletionRetriesPromptly()
	{
		FakeCoordinator coordinator = new( NewJob() )
		{
			OnReport = _ => throw new HttpRequestException( "unreachable" )
		};

		// Without the host stopping, retries would go on for the 2-minute claim.
		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Succeeding() ), FastOptions() with { PollInterval = TimeSpan.FromSeconds( 30 ) } );

		await worker.StartAsync( CancellationToken.None );

		await WaitUntilAsync( () => coordinator.ReportAttempts >= 1 );

		await worker.StopAsync( CancellationToken.None ).WaitAsync( Bound );

		Assert.Equal( 1, coordinator.ReportAttempts );
		Assert.Empty( coordinator.Abandoned );
	}

	[Fact]
	public void OperatorPauseSetsNoFault()
	{
		using SchedulerWorker worker = NewWorker( new FakeCoordinator(), Registry( Succeeding() ), FastOptions() );

		worker.Pause();

		Assert.True( worker.GetInfo().Paused );
		Assert.Null( worker.GetInfo().Fault );
	}

	[Theory]
	[InlineData( "not held" )]
	[InlineData( "unusable claim" )]
	public async Task ARunWhoseReportWasNotRecordedDelaysTheNextClaimByThePollInterval( string failure )
	{
		TimeSpan pollInterval = TimeSpan.FromMilliseconds( 400 );

		// "unusable claim": a claim the worker cannot time, whose one report does not get through
		// — re-offered, as the real coordinator would hand its holder the same run again.
		FakeCoordinator coordinator = failure == "not held"
										? new( NewJob() ) { OnReport = _ => throw new ClaimNotHeldException( "run-1" ) }
										: new( NewJob( claimDuration: TimeSpan.Zero ) )
										{
											ReOffer = true,
											OnReport = _ => throw new HttpRequestException( "unreachable" )
										};

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Succeeding() ), FastOptions() with { PollInterval = pollInterval } );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			TimeSpan gap = Stopwatch.GetElapsedTime( coordinator.ReportTimes.First(), coordinator.ClaimTimes.ElementAt( 1 ) );

			// One attempt for the first run, not retried (the second run may have reported since).
			Assert.Single( coordinator.ClaimsAtReportAttempts, claims => claims == 1 );
			Assert.True( gap >= pollInterval - TimeSpan.FromMilliseconds( 50 ), $"Claimed again after {gap}." );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ARecordedReportIsFollowedByAnImmediateClaim()
	{
		FakeCoordinator coordinator = new( NewJob(), NewJob( run: "next" ) );

		// Waiting a poll interval after the first run would miss the bound.
		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Succeeding() ), FastOptions() with { PollInterval = TimeSpan.FromMinutes( 1 ) } );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, _ ) = await coordinator.ReportOf( "next" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, result );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Theory]
	[InlineData( "refused" )]
	[InlineData( "network" )]
	public async Task AReportThatFailsAndThenGetsThroughIsRecordedWithoutClaimingInBetween( string failure )
	{
		int executions = 0;

		// Re-offered as the real coordinator would: the row still names this worker as holder.
		FakeCoordinator coordinator = new( NewJob() )
		{
			ReOffer = true,
			OnReport = attempt => attempt <= 2 ? throw Failure( failure ) : Task.CompletedTask
		};

		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Counting( () => Interlocked.Increment( ref executions ) ) ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			( string result, _ ) = await coordinator.ReportOf( "run-1" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, result );
			Assert.Equal( 1, executions );
			Assert.Equal( 3, coordinator.ReportAttempts );

			// Every attempt came after the one claim and before any other.
			Assert.Equal( [ 1, 1, 1 ], coordinator.ClaimsAtReportAttempts.ToArray() );
			Assert.Empty( coordinator.Abandoned );
			Assert.False( worker.GetInfo().Paused );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Theory]
	[InlineData( "refused", "timeout" )]
	[InlineData( "refused", "lease" )]
	[InlineData( "network", "timeout" )]
	[InlineData( "network", "lease" )]
	public async Task AReportThatNeverGetsThroughIsDroppedAtTheEarlierOfTheTimeoutAndTheLease( string failure,
																								string first )
	{
		// The deadline that comes first ends the retries: a 1 s timeout under a 2-minute claim, or a
		// claim with a 0.5 s window under a 30 s timeout.
		TimeSpan window = first == "timeout" ? TimeSpan.FromSeconds( 1 ) : ShortClaim - SchedulerWorker.LeaseTolerance;

		FakeCoordinator coordinator = new(
				first == "timeout"
					? NewJob( timeout: window )
					: NewJob( claimDuration: ShortClaim ) )
		{
			OnReport = _ => throw Failure( failure )
		};

		SchedulerWorker worker = NewWorker( coordinator, Registry( Succeeding() ), FastOptions() );

		Stopwatch elapsed = Stopwatch.StartNew();

		await worker.StartAsync( CancellationToken.None );

		try
		{
			// The pump polls again only once the report is given up on.
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.True( elapsed.Elapsed >= window - TimeSpan.FromMilliseconds( 50 ), $"Given up after {elapsed.Elapsed}." );

			// Well before the later deadline (the 2-minute claim, or the 30 s timeout).
			Assert.True( elapsed.Elapsed < TimeSpan.FromSeconds( 8 ), $"Given up after {elapsed.Elapsed}." );

			Assert.True( coordinator.ReportAttempts > 1 );
			Assert.Equal( 0, coordinator.Reports );
			Assert.Empty( coordinator.Abandoned );
			Assert.False( worker.GetInfo().Paused );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Theory]
	[InlineData( "refused" )]
	[InlineData( "network" )]
	public async Task ATimedOutReportIsAttemptedOnce( string failure )
	{
		TaskCompletionSource release = Signal();

		// Ignores cancellation, so it is detached and reported TIMED_OUT past its timeout, with a
		// 2-minute claim that would otherwise allow many retries.
		FakeCoordinator coordinator = new( NewJob( timeout: TimeSpan.FromMilliseconds( 100 ) ) )
		{
			OnReport = _ => throw Failure( failure )
		};

		SchedulerWorker worker = NewWorker( coordinator, Registry( Hanging( release ) ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ClaimsReachedAsync( 2 ).WaitAsync( Bound );

			Assert.Equal( 1, coordinator.ReportAttempts );
			Assert.Equal( 1, worker.GetInfo().DetachedJobs );

			// A detached handler's claim is never released (D27), refused or not.
			Assert.Empty( coordinator.Abandoned );
			Assert.False( worker.GetInfo().Paused );
		}
		finally
		{
			release.TrySetResult();

			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ARefusedClaimIsRetriedAtTheRefusedPollRetryIntervalWithTheFaultShown()
	{
		TimeSpan retryInterval = TimeSpan.FromMilliseconds( 500 );

		FakeCoordinator coordinator = new( NewJob() ) { OnClaim = () => throw new NotAuthorized() };

		// PollInterval is 20 ms: a refused worker must not poll at it.
		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Succeeding() ), FastOptions() with { RefusedPollRetryInterval = retryInterval } );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await WaitUntilAsync( () => worker.GetInfo().Fault is not null );

			WorkerInfo info = worker.GetInfo();

			Assert.Equal( WorkerFaults.ClaimRefused, info.Fault );
			Assert.False( info.Paused );

			await coordinator.ClaimsReachedAsync( 3 ).WaitAsync( Bound );

			long[] claims = coordinator.ClaimTimes.ToArray();

			for( int claim = 1; claim < 3; claim++ )
			{
				TimeSpan gap = Stopwatch.GetElapsedTime( claims[claim - 1], claims[claim] );

				Assert.True( gap >= retryInterval - TimeSpan.FromMilliseconds( 50 ), $"Claimed again after {gap}." );
			}

			Assert.Equal( WorkerFaults.ClaimRefused, worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task ResumeCutsTheRefusedWaitShortAndAnAnsweredClaimClearsTheFault()
	{
		bool refusing = true;

		FakeCoordinator coordinator = new( NewJob() )
		{
			OnClaim = () =>
			{
				if( Volatile.Read( ref refusing ) )
					throw new NotAuthorized();
			}
		};

		// A retry interval far beyond the test's bound: only the state change can end the wait.
		SchedulerWorker worker = NewWorker(
				coordinator, Registry( Succeeding() ),
				FastOptions() with { RefusedPollRetryInterval = TimeSpan.FromMinutes( 5 ) } );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await WaitUntilAsync( () => worker.GetInfo().Fault is not null );

			// The grant lands; an operator nudges the worker rather than waiting the interval out.
			Volatile.Write( ref refusing, false );

			worker.Pause();
			worker.Resume();

			( string result, _ ) = await coordinator.ReportOf( "run-1" ).WaitAsync( Bound );

			Assert.Equal( RunResults.Succeeded, result );
			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}

	[Fact]
	public async Task AClaimFailingOtherwiseIsRetriedAtThePollIntervalWithoutAFault()
	{
		FakeCoordinator coordinator = new() { OnClaim = () => throw new HttpRequestException( "unreachable" ) };

		// The default RefusedPollRetryInterval (1 min) would miss the bound.
		SchedulerWorker worker = NewWorker( coordinator, Registry( Succeeding() ), FastOptions() );

		await worker.StartAsync( CancellationToken.None );

		try
		{
			await coordinator.ClaimsReachedAsync( 3 ).WaitAsync( Bound );

			Assert.Null( worker.GetInfo().Fault );
		}
		finally
		{
			await worker.StopAsync( CancellationToken.None );
		}
	}
}
