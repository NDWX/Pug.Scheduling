using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker
{
	/// <summary>
	/// One claimed job, from its claim to its settled report: the unexecutable guard, the worker's
	/// own lease deadline (D29), the replay of a success the host could not get recorded, the
	/// handler under attendance (heartbeat, timeout, grace, detach), the outcome's classification,
	/// the release of the claim on shutdown (D27), the report with its retries, and remembering or
	/// forgetting a success. One instance per claim, used once.
	///
	/// What stays with <see cref="SchedulerWorker"/>: claiming, pausing, counting active and detached
	/// jobs, and signalling its pump. The worker counts a detached handler through the two callbacks
	/// it passes in: one when the handler is detached, one when it ends.
	/// </summary>
	internal sealed class Work
	{
		private readonly IExecutionCoordinator _coordinator;
		private readonly IScheduledJobHandlerFactoryRegistry _handlerFactories;
		private readonly WorkerOptions _options;
		private readonly int _worker;
		private readonly UnrecordedSuccesses _unrecordedSuccesses;

		// The worker's logger: its category, so hosts' log filters see no difference.
		private readonly ILogger<SchedulerWorker> _logger;

		private readonly ClaimedJob _job;

		// The Stopwatch timestamp taken just before the claim request was sent (D29).
		private readonly long _claimRequested;

		// The worker's count of detached handlers: raised when the handler is detached (returning the
		// new count, for the detach Warning), lowered when the detached handler ends.
		private readonly Func<int> _onDetached;
		private readonly Action _onDetachedEnded;

		public Work( IExecutionCoordinator coordinator,
					IScheduledJobHandlerFactoryRegistry handlerFactories,
					WorkerOptions options,
					int worker,
					UnrecordedSuccesses unrecordedSuccesses,
					ILogger<SchedulerWorker> logger,
					ClaimedJob job,
					long claimRequested,
					Func<int> onDetached,
					Action onDetachedEnded )
		{
			_coordinator = coordinator;
			_handlerFactories = handlerFactories;
			_options = options;
			_worker = worker;
			_unrecordedSuccesses = unrecordedSuccesses;
			_logger = logger;
			_job = job;
			_claimRequested = claimRequested;
			_onDetached = onDetached;
			_onDetachedEnded = onDetachedEnded;
		}

		// What the worker reports for a run. ClaimLost: the handler finished after the claim was lost,
		// so the outcome is never reported, only remembered when it is a success.
		private sealed record RunReport( string Result, JobOutcome Outcome, bool ClaimLost = false );

		// Why a run's claim was lost; see HeartbeatAsync. Every lost claim is abandoned unreported.
		private enum ClaimLoss
		{
			None,

			// The coordinator answered that the claim is no longer held.
			NotHeld,

			// No renewal was confirmed before the worker's deadline for the claim.
			Lapsed
		}

		// The worker's own deadline for a claim (D29): the moment it sent the claim request — or the
		// latest renewal the coordinator confirmed — plus its Window, ClaimDuration less
		// LeaseTolerance. Measured on the Stopwatch (monotonic, unaffected by wall-clock changes)
		// rather than the worker's TimeProvider, which only stamps LastPoll: its waits are raw
		// Task.Delay. Taken before the request, the start can only be earlier than the coordinator's,
		// so skew and round trip move the deadline earlier, never later. A renewal restarts the same
		// duration, since the coordinator renews to its LeaseDuration from its now — which assumes
		// every coordinator instance on the store shares one LeaseDuration.
		//
		// Used by one flow at a time, so it needs no lock: the heartbeat while the run is attended,
		// then ReportAsync, which runs only after the heartbeat has been awaited.
		private sealed class LeaseDeadline
		{
			private long _since;

			// The caller has checked claimDuration against MinimumClaimDuration (Unexecutable).
			public LeaseDeadline( TimeSpan claimDuration, long claimRequested )
				: this( claimRequested, claimDuration - SchedulerWorker.LeaseTolerance )
			{
			}

			private LeaseDeadline( long since, TimeSpan window )
			{
				Window = window;
				_since = since;
			}

			// A deadline already passed, for a claim the worker cannot time: its report is attempted
			// once and never retried.
			public static LeaseDeadline Passed() => new( Stopwatch.GetTimestamp(), TimeSpan.Zero );

			// How long the claim is known to be held after a claim or confirmed renewal is sent.
			public TimeSpan Window { get; }

			// Time left before the claim must be treated as lapsed; zero once it has passed.
			public TimeSpan Remaining
			{
				get
				{
					TimeSpan remaining = Window - Stopwatch.GetElapsedTime( _since );

					return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
				}
			}

			// renewalRequested: the timestamp taken before the confirmed renewal was sent.
			public void Renewed( long renewalRequested ) => _since = renewalRequested;
		}

		// A run's stop signals — its timeout, the loss of its claim, and host shutdown — folded into
		// the one token its handler observes. Owned by the handler until it ends: a detached
		// handler may still hold the token.
		//
		// Cancelling runs the handler's registered callbacks synchronously, on whichever thread
		// triggers it: the timeout's timer, the heartbeat, or the host stopping the worker. Every
		// trigger therefore goes through Cancel, which records the reason first and then contains
		// a throwing callback: it is logged and cannot fault that thread (the token is cancelled
		// regardless).
		//
		// For the same reason the timeout is not a timer-based CancellationTokenSource
		// (CancelAfter): its callbacks would run unguarded on a timer thread, where an exception
		// would be unhandled. The timeout is a Task.Delay awaited by CancelOnTimeoutAsync, which
		// calls the guarded Cancel. The reason flags are set before cancelling so that anyone
		// woken by the token sees why.
		private sealed class RunCancellation : IDisposable
		{
			private readonly CancellationTokenSource _execution = new();
			private readonly CancellationTokenSource _disposed = new();
			private readonly CancellationToken _stopping;
			private readonly CancellationTokenRegistration _stoppingRegistration;
			private readonly Action<Exception> _callbackFaulted;

			private volatile bool _timedOut;
			private volatile ClaimLoss _claimLoss;

			public RunCancellation( TimeSpan timeout, CancellationToken stoppingToken, Action<Exception> callbackFaulted )
			{
				_stopping = stoppingToken;
				_callbackFaulted = callbackFaulted;

				_stoppingRegistration = stoppingToken.Register( Cancel );

				_ = CancelOnTimeoutAsync( timeout );
			}

			public CancellationToken Token => _execution.Token;

			public bool TimedOut => _timedOut;

			public ClaimLoss Loss => _claimLoss;

			public bool ClaimLost => _claimLoss != ClaimLoss.None;

			public bool HostStopping => _stopping.IsCancellationRequested;

			// The one guarded way to stop the run; safe from any thread, and after disposal (a late
			// trigger finds the run already ended).
			private void Cancel()
			{
				try
				{
					_execution.Cancel();
				}
				catch( ObjectDisposedException )
				{
					// The run ended first.
				}
				catch( Exception exception )
				{
					_callbackFaulted( exception );
				}
			}

			// Called once at most, by the heartbeat, which ends right after.
			public void LoseClaim( ClaimLoss loss )
			{
				_claimLoss = loss;

				Cancel();
			}

			// Bounded by the timeout, or by Dispose when the run ends first.
			private async Task CancelOnTimeoutAsync( TimeSpan timeout )
			{
				try
				{
					await Task.Delay( timeout, _disposed.Token ).ConfigureAwait( false );
				}
				catch( OperationCanceledException )
				{
					// The run ended first.
					return;
				}

				_timedOut = true;

				Cancel();
			}

			// Called once the handler has ended (attended or detached): stops the timeout watcher and
			// unhooks the host-stopping registration, so nothing outlives the run.
			public void Dispose()
			{
				_disposed.Cancel();
				_stoppingRegistration.Dispose();
				_execution.Dispose();
				_disposed.Dispose();
			}
		}

		private void LogCallbackFault( Exception exception ) =>
			_logger.LogWarning( exception,
								"Worker {Claimant}/{Worker} run {Run}: a cancellation callback threw while the run was being cancelled.",
								_options.Claimant, _worker, _job.Context.Run );

		private RunReport Completed( JobOutcome outcome )
		{
			outcome ??= new JobOutcome { Successful = false, Error = "Job handler returned no outcome." };

			if( !outcome.Successful )
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) was unsuccessful: {Error}",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
						_job.Context.JobType, outcome.Error );

			return new RunReport( outcome.Successful ? RunResults.Succeeded : RunResults.Failed, outcome );
		}

		// The failed report for a claim the worker cannot execute safely, or null when it can: a
		// timeout no timer accepts, or a claim duration the worker cannot time. The claim duration
		// must be at least MinimumClaimDuration, so the window after LeaseTolerance leaves room for
		// renewals (a coordinator enforces a far larger minimum, but a remote one is not trusted;
		// an old coordinator sends none, i.e. zero), and within TimerBounds, or it could not be
		// waited on.
		private RunReport Unexecutable()
		{
			string error;

			if( !TimerBounds.Contains( _job.Timeout ) )
				error = $"Job timeout {_job.Timeout} is outside the supported range (0 to {TimerBounds.Maximum}); " +
						"the job was not executed.";
			else if( _job.ClaimDuration < SchedulerWorker.MinimumClaimDuration || _job.ClaimDuration > TimerBounds.Maximum )
				error = $"Claim duration {_job.ClaimDuration} is outside the range the worker can time ({SchedulerWorker.MinimumClaimDuration} " +
						$"to {TimerBounds.Maximum}); the job was not executed.";
			else
				return null;

			_logger.LogError(
					"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) not executed: {Error}",
					_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.Context.JobType, error );

			return new RunReport( RunResults.Failed, new JobOutcome { Successful = false, Error = error } );
		}

		private RunReport TimedOut( string error )
		{
			_logger.LogWarning(
					"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) timed out after {Duration}: {Error}",
					_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
					_job.Context.JobType, _job.Timeout, error );

			return new RunReport( RunResults.TimedOut, new JobOutcome { Successful = false, Error = error } );
		}

		// Third-party handler code: any escaping exception is an unsuccessful outcome.
		private RunReport Threw( Exception exception )
		{
			_logger.LogWarning( exception,
								"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) threw.",
								_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
								_job.Context.JobType );

			return new RunReport( RunResults.Failed, new JobOutcome { Successful = false, Error = exception.ToString() } );
		}

		private async Task DisposeHandlerAsync( IScheduledJobHandler handler, ScheduledJobContext context )
		{
			try
			{
				if( handler is IAsyncDisposable asyncDisposable )
					await asyncDisposable.DisposeAsync().ConfigureAwait( false );
				else if( handler is IDisposable disposable )
					disposable.Dispose();
			}
			catch( Exception exception )
			{
				_logger.LogWarning( exception,
									"Worker {Claimant}/{Worker} failed to dispose the handler of run {Run} of schedule {Schedule} ({JobType}).",
									_options.Claimant, _worker, context.Run, context.Schedule, context.JobType );
			}
		}

		// Abandoning without reporting: the claim was lost (a peer may already own the occurrence) or
		// the host is stopping. At-least-once semantics re-execute the occurrence once the lease
		// expires.
		private void LogAbandoned( RunCancellation cancellation, string detail ) =>
			_logger.LogWarning(
					"Worker {Claimant}/{Worker} abandoned run {Run} of schedule {Schedule}: {Reason}{Detail}.",
					_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
					cancellation.Loss switch
					{
						ClaimLoss.NotHeld => "claim no longer held",
						ClaimLoss.Lapsed => "claim lapsed unrenewed",
						_ => "host stopping"
					},
					detail );

		// A claim lost to refused renewals is an Error naming the grant: with RENEW_CLAIM truly
		// missing, every run longer than the window (ClaimDuration less LeaseTolerance) ends this way
		// and is executed again, so it is what an operator must alert on. Lapses for any other reason
		// are Warnings.
		private void LoseLapsedClaim( bool refused, Action<ClaimLoss> loseClaim )
		{
			if( refused )
				_logger.LogError(
						"Worker {Claimant}/{Worker} lost its claim on run {Run} of schedule {Schedule}: the coordinator " +
						"refused its renewals for the whole claim duration of {ClaimDuration}. Grant {Operation} to " +
						"the claimant, or check its security provider; the run will be executed again.",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.ClaimDuration,
						CoordinatorSecurityOperations.RenewClaim );
			else
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} could not renew its claim on run {Run} of schedule {Schedule} " +
						"within its claim duration of {ClaimDuration}; the claim is treated as lost.",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.ClaimDuration );

			loseClaim( ClaimLoss.Lapsed );
		}

		// Creates the run's handler, executes it and disposes it once it ends — attended or
		// detached, so exactly once. Missing or failing factories are materialized as unsuccessful
		// outcomes; an exception escaping the handler propagates to the caller's classification.
		// Disposal failures never change the outcome.
		private async Task<JobOutcome> RunHandlerAsync( CancellationToken cancellationToken )
		{
			ScheduledJobContext context = _job.Context;

			IScheduledJobHandlerFactory factory = _handlerFactories.GetFactory( context.JobType );

			if( factory is null )
			{
				_logger.LogError(
						"Worker {Claimant}/{Worker} has no handler registered for job type {JobType} (run {Run} of schedule {Schedule}).",
						_options.Claimant, _worker, context.JobType, context.Run, context.Schedule );

				return new JobOutcome
				{
					Successful = false,
					Error = $"No handler registered for job type '{context.JobType}'."
				};
			}

			IScheduledJobHandler handler;

			try
			{
				handler = factory.Create( context );
			}
			catch( Exception exception )
			{
				_logger.LogError( exception,
								"Worker {Claimant}/{Worker} handler factory failed for job type {JobType} (run {Run} of schedule {Schedule}).",
								_options.Claimant, _worker, context.JobType, context.Run, context.Schedule );

				return new JobOutcome { Successful = false, Error = exception.ToString() };
			}

			if( handler is null )
			{
				_logger.LogError(
						"Worker {Claimant}/{Worker} handler factory for job type {JobType} returned no handler (run {Run} of schedule {Schedule}).",
						_options.Claimant, _worker, context.JobType, context.Run, context.Schedule );

				return new JobOutcome { Successful = false, Error = "Job handler factory returned no handler." };
			}

			try
			{
				return await handler.ExecuteAsync( context, _job.Timeout, cancellationToken ).ConfigureAwait( false );
			}
			finally
			{
				await DisposeHandlerAsync( handler, context ).ConfigureAwait( false );
			}
		}

		// Runs the handler off the pump's thread (Task.Run): the factory and the handler's synchronous
		// prefix are third-party code, so one that blocks synchronously cannot stall the pump, the
		// heartbeat or the timeout. The token is the run's, not the worker's, so only a stop signal
		// of this run cancels it.
		private Task<JobOutcome> StartHandler( CancellationToken cancellationToken ) =>
			Task.Run( () => RunHandlerAsync( cancellationToken ), CancellationToken.None );

		// Keeps the claim held while the run is attended, and decides when it is lost (D29). Each beat
		// waits, then renews; a renewal the coordinator confirms restarts the lease from the moment it
		// was sent. The claim is lost — loseClaim stops the run, and the heartbeat ends — when:
		// - the coordinator answers false (no longer held);
		// - the lease deadline arrives with no confirmed renewal (lapsed): renewals failed, hung or
		//   were refused for the whole window, so a peer may already have adopted the run.
		// Any other failure is unconfirmed: logged and retried at the next beat. That includes a
		// refusal (NotAuthorized): a fail-closed security provider refuses during an outage exactly as
		// a missing grant does, so it is retried within the lease rather than acted on.
		//
		// The beat is HeartbeatInterval capped at a third of the window (ClaimDuration less
		// LeaseTolerance), so even a HeartbeatInterval at or above the lease leaves a renewal and a
		// genuine retry within each window. When the remaining window is no longer than a beat, the
		// wait is the whole remainder and the claim is lapsed after it, without re-reading the
		// deadline: Task.Delay and WaitAsync work in whole milliseconds and may fire a little before
		// the Stopwatch deadline, and a remainder read as tiny-positive would spin renewal after
		// renewal. For the same reason a renewal still pending when its wait (the remaining window)
		// times out is the lapse itself, not a failure to retry: it is the one renewal that may still
		// land after the worker gave the claim up. stopToken ending a delay or a renewal — which
		// ExecuteAttendedAsync cancels when watching ends, then awaits the heartbeat — is the normal
		// way out, so a hung call never delays the detach.
		private async Task HeartbeatAsync( LeaseDeadline lease, Action<ClaimLoss> loseClaim, CancellationToken stopToken )
		{
			TimeSpan thirdOfWindow = lease.Window / 3;
			TimeSpan beat = thirdOfWindow < _options.HeartbeatInterval ? thirdOfWindow : _options.HeartbeatInterval;

			// Whether the latest renewal attempt was refused, so a lapse can say why.
			bool refused = false;

			try
			{
				while( true )
				{
					TimeSpan remaining = lease.Remaining;

					if( remaining > beat )
					{
						await Task.Delay( beat, stopToken ).ConfigureAwait( false );

						// What is left after a full beat; at most a severe stall (a long GC pause)
						// makes it zero.
						remaining = lease.Remaining;
					}
					else
					{
						// The last stretch: nothing is renewed in it, so the claim lapses at its end.
						await Task.Delay( remaining, stopToken ).ConfigureAwait( false );

						remaining = TimeSpan.Zero;
					}

					if( remaining <= TimeSpan.Zero )
					{
						LoseLapsedClaim( refused, loseClaim );

						return;
					}

					long renewalRequested = Stopwatch.GetTimestamp();

					Task<bool> renewal = null;
					bool renewed;

					try
					{
						renewal = _coordinator.RenewClaimAsync( _job.Context.Run, _options.Claimant, _worker );

						renewed = await renewal.WaitAsync( remaining, stopToken ).ConfigureAwait( false );
					}
					catch( OperationCanceledException ) when( stopToken.IsCancellationRequested )
					{
						return;
					}
					catch( TimeoutException ) when( renewal is { IsCompleted: false } )
					{
						// Still pending at the deadline: the lapse itself (see above).
						LoseLapsedClaim( refused, loseClaim );

						return;
					}
					catch( NotAuthorized exception )
					{
						refused = true;

						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} was refused the renewal of its claim on run {Run} of " +
											"schedule {Schedule} (grant {Operation} to the claimant, or check its security " +
											"provider); retrying while the claim stands ({Remaining} left).",
											_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
											CoordinatorSecurityOperations.RenewClaim, lease.Remaining );

						continue;
					}
					catch( Exception exception )
					{
						refused = false;

						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} failed to renew its claim on run {Run} of schedule " +
											"{Schedule}; retrying while the claim stands ({Remaining} left).",
											_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
											lease.Remaining );

						continue;
					}

					if( !renewed )
					{
						_logger.LogWarning(
								"Worker {Claimant}/{Worker} no longer holds its claim on run {Run} of schedule {Schedule}.",
								_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule );

						loseClaim( ClaimLoss.NotHeld );

						return;
					}

					lease.Renewed( renewalRequested );

					refused = false;

					_logger.LogDebug( "Worker {Claimant}/{Worker} renewed claim of run {Run}.",
									_options.Claimant, _worker, _job.Context.Run );
				}
			}
			catch( OperationCanceledException ) when( stopToken.IsCancellationRequested )
			{
				// Heartbeat stopped with the job — expected.
			}
		}

		// Watches a running handler until it finishes or the worker gives up on it. Returns true if
		// it finished (the caller classifies its outcome); false if it is still running after being
		// stopped and given its grace period (the caller detaches it).
		//
		// Heartbeat: renews the claim for as long as the worker watches, including the grace. A
		// claim the heartbeat finds lost (not held, or lapsed unrenewed) stops the run through
		// LoseClaim.
		//
		// Phase 1, run: WaitForAsync( run, InfiniteTimeSpan, cancellation.Token ) ends when the
		// handler finishes or the run is stopped, i.e. cancellation.Token is cancelled: the schedule
		// timeout (RunCancellation's timeout watcher), claim lost, or the host stopping.
		// InfiniteTimeSpan means no limit beyond those.
		//
		// Phase 2, grace: only when the run was stopped while the handler was still running. The
		// handler's token has just been cancelled, and it gets CancellationGrace to notice and
		// finish. That wait uses CancellationToken.None deliberately, so a cooperative handler gets
		// its grace even during shutdown.
		private async Task<bool> ExecuteAttendedAsync( Task<JobOutcome> run, RunCancellation cancellation, LeaseDeadline lease )
		{
			using CancellationTokenSource heartbeatStop = new();

			Task heartbeat = HeartbeatAsync( lease, cancellation.LoseClaim, heartbeatStop.Token );

			try
			{
				// Phase 1: until the handler finishes or the run is stopped (timeout, claim lost,
				// host stopping); no other limit.
				await SchedulerWorker.WaitForAsync( run, Timeout.InfiniteTimeSpan, cancellation.Token ).ConfigureAwait( false );

				// Phase 2: stopped but still running; bounded by CancellationGrace, never cancelled.
				if( !run.IsCompleted )
					await SchedulerWorker.WaitForAsync( run, _options.CancellationGrace, CancellationToken.None ).ConfigureAwait( false );

				return run.IsCompleted;
			}
			finally
			{
				// The heartbeat ran through the grace because the handler may still finish
				// legitimately, so the claim must stay held. It stops only now, when watching ends
				// (finished or detached), and is awaited so no renewal happens after the run is
				// reported. It ends promptly: even a renewal call still in flight is waited on with
				// this token.
				await heartbeatStop.CancelAsync().ConfigureAwait( false );

				await heartbeat.ConfigureAwait( false );
			}
		}

		// The handler ended (normally, by exception, or by honouring cancellation); the run is
		// already complete, so GetResult does not block.
		private RunReport ClassifyFinished( Task<JobOutcome> run, RunCancellation cancellation )
		{
			RunReport report;

			// Catch order is the precedence: a timeout firing during a shutdown-initiated grace reports
			// TIMED_OUT, the same as when the handler ends within the grace. A cancellation exception
			// caused by claim loss or shutdown abandons; any other exception is a failed run.
			try
			{
				report = Completed( run.GetAwaiter().GetResult() );
			}
			catch( OperationCanceledException ) when( cancellation.TimedOut )
			{
				report = TimedOut( $"Execution exceeded the schedule timeout of {_job.Timeout}." );
			}
			catch( OperationCanceledException ) when( cancellation.ClaimLost || cancellation.HostStopping )
			{
				LogAbandoned( cancellation, "" );

				return null;
			}
			catch( Exception exception )
			{
				report = Threw( exception );
			}

			// The handler completed, but not under the claim: even a clean outcome is not reported (a
			// peer may own the run). It is returned marked ClaimLost rather than dropped, so the caller
			// can still remember a success: the work was done.
			if( cancellation.ClaimLost )
			{
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} completed run {Run} of schedule {Schedule} after losing its claim; " +
						"outcome not reported.",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule );

				return report with { ClaimLost = true };
			}

			return report;
		}

		// The handler was stopped and did not finish within the grace. Only a timeout is reported;
		// a lost claim or a stopping host abandons the run, as when the handler honours cancellation.
		private RunReport ClassifyDetached( RunCancellation cancellation )
		{
			if( cancellation.TimedOut && !cancellation.ClaimLost )
				return TimedOut( $"Handler did not stop within {_options.CancellationGrace} of cancellation; detached." );

			LogAbandoned( cancellation, "; handler detached" );

			return null;
		}

		// What to report for a run no longer attended; null abandons it without reporting, as does a
		// report marked ClaimLost (an outcome reached after the claim was lost, kept only so a
		// success can be remembered).
		//
		// Precedence when several stop reasons coincide: timeout > claim lost / host stopping >
		// exception. A lost claim is never reported (a peer may already own the occurrence).
		private RunReport ClassifyOutcome( Task<JobOutcome> run, bool finished, RunCancellation cancellation ) =>
			finished ? ClassifyFinished( run, cancellation ) : ClassifyDetached( cancellation );

		// Reports the run's outcome (D29), and returns whether it was recorded. What the coordinator
		// answers decides what follows:
		// - recorded: done;
		// - not held (ClaimNotHeldException, UnknownRunException): the run was adopted, finished or
		//   deleted; the outcome is dropped;
		// - anything else, a refusal (NotAuthorized) included: retried every PollInterval until the
		//   lease deadline, then dropped with an Error. A refusal is retried rather than acted on,
		//   because a fail-closed security provider refuses during an outage exactly as a missing
		//   grant does; no claim is released and the worker never pauses.
		//
		// The lease deadline ends the retries because past it a peer may adopt the run and refuse the
		// report (until then a lapsed holder's report is still accepted). TIMED_OUT reports get the same window: the
		// coordinator never compares a run with its timeout, so a late report is as good as an
		// early one. The pump claims nothing while retrying, then waits PollInterval after a
		// dropped report; a dropped success is remembered by the caller and replayed rather than
		// re-run.
		//
		// Retries have the heartbeat's shape. When the lease's remainder is no longer than
		// PollInterval, the wait is the whole remainder and the attempt after it is the last: the
		// deadline is not re-read, since a timer may fire a little before the Stopwatch deadline and
		// the remainder would then read tiny-positive and spin. The first attempt is made even past
		// the deadline: the coordinator accepts a lapsed holder's report until a peer adopts the run
		// (D23). The host stopping ends the retries at once; a single attempt in flight is bounded
		// only by the coordinator's own call (the transport's and the store's timeouts), which a
		// host's shutdown budget must allow for.
		private async Task<bool> ReportAsync( RunReport report, Stopwatch stopwatch, LeaseDeadline lease,
											CancellationToken stoppingToken )
		{
			bool lastAttempt = false;

			while( true )
			{
				Exception unconfirmed;

				try
				{
					await _coordinator.ReportCompletionAsync(
										_job.Context.Run, _options.Claimant, _worker, report.Result, report.Outcome )
									.ConfigureAwait( false );

					_logger.LogInformation(
							"Worker {Claimant}/{Worker} finished run {Run} of schedule {Schedule} ({JobType}) " +
							"with result {Result} in {Duration}.",
							_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
							_job.Context.JobType, report.Result, stopwatch.Elapsed );

					return true;
				}
				catch( Exception exception ) when( exception is ClaimNotHeldException or UnknownRunException )
				{
					_logger.LogWarning( exception,
										"Worker {Claimant}/{Worker} no longer holds run {Run} of schedule {Schedule}; " +
										"its {Result} outcome was not recorded.",
										_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, report.Result );

					return false;
				}
				catch( Exception exception )
				{
					unconfirmed = exception;
				}

				// Names the grant when the coordinator refused, so the logs say what to fix.
				string refusal = unconfirmed is NotAuthorized
									? $" The coordinator refused it: grant {CoordinatorSecurityOperations.ReportCompletion} " +
									"to the claimant, or check its security provider."
									: "";

				TimeSpan remaining = lease.Remaining;

				if( !lastAttempt && remaining > TimeSpan.Zero && !stoppingToken.IsCancellationRequested )
				{
					lastAttempt = remaining <= _options.PollInterval;

					_logger.LogWarning( unconfirmed,
										"Worker {Claimant}/{Worker} failed to report completion of run {Run} of schedule " +
										"{Schedule}; retrying for up to {Remaining}.{Refusal}",
										_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, remaining,
										refusal );

					// Bounded by PollInterval or, for the last attempt, the lease's remainder; the host
					// stopping cuts it short, and the report is then given up below.
					try
					{
						await Task.Delay( lastAttempt ? remaining : _options.PollInterval, stoppingToken )
								.ConfigureAwait( false );

						continue;
					}
					catch( OperationCanceledException )
					{
						// The host is stopping: give up below.
					}
				}

				_logger.LogError( unconfirmed,
								"Worker {Claimant}/{Worker} could not report completion of run {Run} of schedule {Schedule} " +
								"({Reason}); its {Result} outcome was dropped.{Refusal}",
								_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
								stoppingToken.IsCancellationRequested ? "the host stopped"
								// A claim the worker could not time (Unexecutable) had no window to retry in.
								: lease.Window <= TimeSpan.Zero ? "its claim could not be timed, so it was not retried"
								: "its claim lapsed",
								report.Result, refusal );

				return false;
			}
		}

		// Best effort: releases the claim so a peer adopts the run without waiting for the lease,
		// bounded by AbandonTimeout. A failure only means the lease lapses on its own.
		private async Task TryAbandonClaimAsync()
		{
			try
			{
				bool released = await _coordinator.AbandonClaimAsync( _job.Context.Run, _options.Claimant, _worker )
												.WaitAsync( _options.AbandonTimeout ).ConfigureAwait( false );

				_logger.LogInformation(
						"Worker {Claimant}/{Worker} released its claim on run {Run} of schedule {Schedule}: {Released}.",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule,
						released ? "peers may adopt it now" : "it was no longer held" );
			}
			catch( Exception exception )
			{
				_logger.LogWarning( exception,
									"Worker {Claimant}/{Worker} could not release its claim on run {Run} of schedule {Schedule}; " +
									"it lapses with the lease.",
									_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule );
			}
		}

		// Observes the detached handler until it ends, however long that takes, only to log it and
		// release what it held. Its outcome is never reported: the run was already reported
		// (TIMED_OUT) or abandoned when it was detached.
		private async Task AwaitDetachedAsync( Task<JobOutcome> run, RunCancellation cancellation, Stopwatch stopwatch )
		{
			try
			{
				await run.ConfigureAwait( false );

				_logger.LogWarning(
						"Worker {Claimant}/{Worker} detached handler of run {Run} of schedule {Schedule} ({JobType}) " +
						"ended after {Duration}.",
						_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.Context.JobType,
						stopwatch.Elapsed );
			}
			catch( Exception exception )
			{
				_logger.LogWarning( exception,
									"Worker {Claimant}/{Worker} detached handler of run {Run} of schedule {Schedule} ({JobType}) " +
									"ended after {Duration} with a fault.",
									_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.Context.JobType,
									stopwatch.Elapsed );
			}
			finally
			{
				// The handler has ended, so nothing can still observe the token.
				cancellation.Dispose();

				// Lowers the worker's count and wakes a pump suspended at the detached limit so it can
				// claim again.
				_onDetachedEnded();
			}
		}

		// Leaves a handler that ignored cancellation to end on its own. Nothing bounds how long that
		// takes; MaxDetachedHandlers bounds how many may accumulate. Ownership moves to
		// AwaitDetachedAsync: the handler itself is disposed by RunHandlerAsync when it ends, and the
		// run's RunCancellation (whose token the handler may still hold) is disposed by
		// AwaitDetachedAsync, never earlier.
		private void Detach( Task<JobOutcome> run, RunCancellation cancellation, Stopwatch stopwatch )
		{
			int detached = _onDetached();

			_logger.LogWarning(
					"Worker {Claimant}/{Worker} detached the handler of run {Run} of schedule {Schedule} ({JobType}): " +
					"it did not stop within {CancellationGrace} of cancellation. {Detached} handler(s) detached.",
					_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.Context.JobType,
					_options.CancellationGrace, detached );

			_ = AwaitDetachedAsync( run, cancellation, stopwatch );
		}

		// Executes the claimed job under attendance, then reports its outcome, abandons it, or —
		// when the handler would not stop — detaches it.
		//
		// Steps: reject unusable claim parameters (reported as failed, handler never started); start
		// the handler; attend it (ExecuteAttendedAsync: heartbeat, timeout, grace); classify the
		// result; then either dispose the run's cancellation (handler ended) or detach (handler still
		// running); finally report, or for an abandoned run release the claim if that is safe. Report
		// and claim release come after Detach so a slow coordinator cannot delay detaching.
		//
		// Returns whether the run's report was recorded; the pump waits before claiming again when
		// it was not.
		public async Task<bool> ExecuteAsync( CancellationToken stoppingToken )
		{
			Stopwatch stopwatch = Stopwatch.StartNew();

			_logger.LogInformation(
					"Worker {Claimant}/{Worker} claimed run {Run} of schedule {Schedule} ({JobType}).",
					_options.Claimant, _worker, _job.Context.Run, _job.Context.Schedule, _job.Context.JobType );

			// Parameters the worker cannot honour (they may come from a remote coordinator) fail the
			// run; they must not escape the pump. With no claim it can time, the failure is reported
			// once and never retried.
			RunReport unexecutable = Unexecutable();

			if( unexecutable is not null )
				return await ReportAsync( unexecutable, stopwatch, LeaseDeadline.Passed(), stoppingToken )
								.ConfigureAwait( false );

			LeaseDeadline lease = new( _job.ClaimDuration, _claimRequested );

			RunReport report;

			// An occurrence whose success this host could not get recorded is not run again: its
			// remembered outcome is replayed under this claim (D29). Nothing executes, so there is
			// no heartbeat; the report's retries are bounded by this claim's lease. An entry whose
			// job type or payload has since changed is dropped by the store, and the job runs.
			if( _unrecordedSuccesses.TryGet( _job.Context, out JobOutcome remembered, out bool definitionChanged ) )
			{
				_logger.LogInformation(
						"Worker {Claimant}/{Worker} is replaying the remembered successful outcome of occurrence " +
						"{ScheduledFor} of schedule {Schedule} (run {Run}); handler not run.",
						_options.Claimant, _worker, _job.Context.ScheduledFor, _job.Context.Schedule, _job.Context.Run );

				report = new RunReport( RunResults.Succeeded, remembered );
			}
			else
			{
				if( definitionChanged )
					_logger.LogWarning(
							"Worker {Claimant}/{Worker} remembered a successful outcome of occurrence {ScheduledFor} of " +
							"schedule {Schedule}, but the schedule's definition changed since; executing run {Run}.",
							_options.Claimant, _worker, _job.Context.ScheduledFor, _job.Context.Schedule,
							_job.Context.Run );

				RunCancellation cancellation = new(
						_job.Timeout, stoppingToken, exception => LogCallbackFault( exception ) );

				Task<JobOutcome> run = StartHandler( cancellation.Token );

				// A snapshot: a handler completing between here and Detach below is still detached,
				// and reported as TIMED_OUT.
				bool finished = await ExecuteAttendedAsync( run, cancellation, lease ).ConfigureAwait( false );

				report = ClassifyOutcome( run, finished, cancellation );

				// The run's cancellation is the handler's until it ends: disposed here if it ended,
				// otherwise handed to Detach, which disposes it when the handler ends. Its reason
				// flags stay readable after disposal.
				if( finished )
					cancellation.Dispose();
				else
					Detach( run, cancellation, stopwatch );

				// A null report means abandoned (claim lost, or host stopping) with no outcome.
				if( report is null )
				{
					// The host is stopping and the claim was not lost: release it, so a peer adopts the
					// run without waiting out the lease (D27). Only a stopped handler's claim: a
					// detached one is still running, so its claim must lapse rather than let a peer
					// adopt the run while the old handler still works on it. A claim no longer held has
					// nothing to release; a lapsed one is left to its lease.
					if( finished && cancellation.HostStopping && !cancellation.ClaimLost )
						await TryAbandonClaimAsync().ConfigureAwait( false );

					return false;
				}
			}

			// An outcome reached after the claim was lost is never reported (a peer may own the run),
			// but it still goes through the block below, so a success is remembered.
			bool recorded = !report.ClaimLost &&
							await ReportAsync( report, stopwatch, lease, stoppingToken ).ConfigureAwait( false );

			// The one place successes are remembered and forgotten. A success is remembered until it
			// is recorded, so a later claim of the occurrence on this host replays it rather than
			// running the job again: dropped at the lease deadline, answered "not held" (a peer owns
			// the run now, but if it fails, its retry is the same occurrence), or completed after the
			// claim was lost (the work was done). Failures and TIMED_OUT are never remembered:
			// running them again is what a retry is for.
			if( report.Result == RunResults.Succeeded )
			{
				if( recorded )
				{
					_unrecordedSuccesses.Forget( _job.Context );

					_logger.LogDebug(
							"Worker {Claimant}/{Worker} no longer remembers occurrence {ScheduledFor} of schedule " +
							"{Schedule}: its success is recorded.",
							_options.Claimant, _worker, _job.Context.ScheduledFor, _job.Context.Schedule );
				}
				else
				{
					_unrecordedSuccesses.Remember( _job.Context, report.Outcome );

					_logger.LogInformation(
							"Worker {Claimant}/{Worker} remembers the unrecorded successful outcome of occurrence " +
							"{ScheduledFor} of schedule {Schedule}; it will be replayed if claimed again on this host.",
							_options.Claimant, _worker, _job.Context.ScheduledFor, _job.Context.Schedule );
				}
			}

			return recorded;
		}
	}
}
