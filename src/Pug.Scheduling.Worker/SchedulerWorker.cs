using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker
{
	/// <summary>
	/// Execution pump: polls <see cref="IExecutionCoordinator"/> for due jobs, dispatches each
	/// to the <see cref="IScheduledJobHandler"/> created by the job type's
	/// <see cref="IScheduledJobHandlerFactory"/> with timeout enforcement and claim heartbeats, and reports outcomes back. The worker knows nothing of schedules, stores, or
	/// leases beyond the coordinator surface — it can be hosted in-process with the scheduler or
	/// against a remote coordinator transport, unchanged.
	///
	/// Handler factories and handlers are treated as third-party code: any escaping exception or null
	/// outcome is materialized as an unsuccessful <see cref="JobOutcome"/>, and no failure of one
	/// job ever stops the poll loop. Cancellation is not assumed to be honored: a handler still
	/// running <see cref="WorkerOptions.CancellationGrace"/> after its run was stopped (timeout,
	/// claim lost, host stopping) is detached and left to end on its own, and the worker moves on.
	/// The worker attends a single job at once; at <see cref="WorkerOptions.MaxDetachedHandlers"/>
	/// detached handlers it stops claiming until one ends.
	///
	/// The worker times its claim itself (D29): from <see cref="ClaimedJob.ClaimDuration"/>, less
	/// <see cref="LeaseTolerance"/>, on its own clock. Unrenewed through that deadline, the claim is
	/// lost: the handler is stopped and nothing is reported. The worker never pauses itself: a
	/// coordinator refusal (<see cref="NotAuthorized"/>) is retried like any failure that did not get
	/// through, since a fail-closed security provider whose backend is down refuses exactly as a
	/// missing grant does. Refused claims are retried at
	/// <see cref="WorkerOptions.RefusedPollRetryInterval"/> and show as
	/// <see cref="WorkerFaults.ClaimRefused"/> in <see cref="WorkerInfo.Fault"/> until a claim is
	/// answered again.
	/// </summary>
	public sealed class SchedulerWorker : BackgroundService, ISchedulerWorker
	{
		private readonly IExecutionCoordinator _coordinator;
		private readonly IScheduledJobHandlerFactoryRegistry _handlerFactories;
		private readonly WorkerOptions _options;
		private readonly int _worker;
		private readonly TimeProvider _timeProvider;
		private readonly ILogger<SchedulerWorker> _logger;

		// The worker's group filter as logged: its job type, or "any".
		private readonly string _jobTypeFilter;

		/// <summary>
		/// How much earlier than <see cref="ClaimedJob.ClaimDuration"/> says the worker treats an
		/// unrenewed claim as lapsed. The worker measures the claim from just before its request, so
		/// round trip and clock skew already put its deadline before the coordinator's; the tolerance
		/// covers what that does not — the coordinator's clock (the database's by default) running
		/// slightly fast or stepping forward — and leaves the handler's cancellation a margin. A claim
		/// whose duration does not exceed it is failed unexecuted.
		/// </summary>
		public static readonly TimeSpan LeaseTolerance = TimeSpan.FromMilliseconds( 500 );

		/// <summary>
		/// The shortest <see cref="ClaimedJob.ClaimDuration"/> the worker executes a run under: twice
		/// <see cref="LeaseTolerance"/>, leaving a window at least as long as the tolerance. The
		/// coordinator requires far more (<c>ExecutionCoordinatorOptions.MinimumLeaseDuration</c>);
		/// this guards against a coordinator of another version.
		/// </summary>
		public static readonly TimeSpan MinimumClaimDuration = LeaseTolerance * 2;

		private readonly object _stateLock = new();
		private bool _paused;

		// WorkerFaults.ClaimRefused while the coordinator refuses this worker's claims; null otherwise.
		// Written by the pump only (ClaimAsync), under _stateLock for GetInfo.
		private string _fault;
		private DateTime? _lastPoll;
		private int _activeJobs;
		private int _detachedJobs;

		// Whether the pump has stopped claiming for detached handlers; touched by the pump only,
		// so the Error is logged once per episode.
		private bool _claimingSuspended;

		// Completed (and replaced) on pause/resume and whenever a detached handler ends, so the
		// loop's waits are interrupted immediately (FetchWorker precedent).
		private TaskCompletionSource _stateChanged = NewSignal();

		/// <param name="coordinator">The worker's only link to the scheduler.</param>
		/// <param name="handlerFactories">Handler factories by job type.</param>
		/// <param name="options">The worker's group configuration; see <see cref="WorkerOptions.Validate"/>.</param>
		/// <param name="number">
		/// The worker's number, 1 or greater and unique within the host: the coordinator holds this
		/// worker's claims as "Claimant/number", and a restarted worker presenting the same claimant
		/// and number re-takes its own claims.
		/// </param>
		/// <param name="timeProvider">The worker's clock (<see cref="WorkerInfo.LastPoll"/>).</param>
		/// <param name="logger">Logger.</param>
		public SchedulerWorker( IExecutionCoordinator coordinator,
								IScheduledJobHandlerFactoryRegistry handlerFactories,
								WorkerOptions options,
								int number,
								TimeProvider timeProvider,
								ILogger<SchedulerWorker> logger )
		{
			_coordinator = coordinator ?? throw new ArgumentNullException( nameof(coordinator) );
			_handlerFactories = handlerFactories ?? throw new ArgumentNullException( nameof(handlerFactories) );
			_options = options ?? throw new ArgumentNullException( nameof(options) );

			_options.Validate();

			if( number < 1 )
				throw new ArgumentOutOfRangeException( nameof(number), number, "Worker number must be 1 or greater." );

			_worker = number;
			_jobTypeFilter = _options.JobType ?? "any";
			_timeProvider = timeProvider ?? throw new ArgumentNullException( nameof(timeProvider) );
			_logger = logger ?? throw new ArgumentNullException( nameof(logger) );
		}

		// The worker's host-wide number.
		public int Number => _worker;

		private static TaskCompletionSource NewSignal() =>
			new( TaskCreationOptions.RunContinuationsAsynchronously );

		private void SignalStateChanged() =>
			Interlocked.Exchange( ref _stateChanged, NewSignal() ).TrySetResult();

		public WorkerInfo GetInfo()
		{
			lock( _stateLock )
				return new WorkerInfo
				{
					Claimant = _options.Claimant,
					Worker = _worker,
					JobType = _options.JobType,
					Paused = _paused,
					Fault = _fault,
					LastPoll = _lastPoll,
					ActiveJobs = Volatile.Read( ref _activeJobs ),
					DetachedJobs = Volatile.Read( ref _detachedJobs )
				};
		}

		public void Pause()
		{
			lock( _stateLock )
				_paused = true;

			_logger.LogInformation( "Worker {Claimant}/{Worker} (job type {JobType}) paused.",
									_options.Claimant, _worker, _jobTypeFilter );

			SignalStateChanged();
		}

		public void Resume()
		{
			lock( _stateLock )
				_paused = false;

			_logger.LogInformation( "Worker {Claimant}/{Worker} (job type {JobType}) resumed.",
									_options.Claimant, _worker, _jobTypeFilter );

			SignalStateChanged();
		}

		private bool IsPaused
		{
			get
			{
				lock( _stateLock )
					return _paused;
			}
		}

		protected override async Task ExecuteAsync( CancellationToken stoppingToken )
		{
			_logger.LogInformation( "Worker {Claimant}/{Worker} (job type {JobType}) started.",
									_options.Claimant, _worker, _jobTypeFilter );

			try
			{
				await PumpAsync( stoppingToken ).ConfigureAwait( false );
			}
			finally
			{
				_logger.LogInformation( "Worker {Claimant}/{Worker} (job type {JobType}) stopping.",
										_options.Claimant, _worker, _jobTypeFilter );
			}
		}

		// The poll loop; ends only when the host stops the worker. Each iteration is one of:
		// - not claiming (paused, or at the detached limit): wait for a state change (resume, a
		//   detached handler ending) or the host stopping, with no time limit;
		// - claimed a job: attend it to completion or detachment and settle its report; then poll
		//   again at once if the report was recorded (more work may be due), otherwise wait
		//   PollInterval first (see below);
		// - nothing due (or the claim failed): wait PollInterval, cut short by a state change;
		// - the claim was refused: wait RefusedPollRetryInterval instead, likewise cut short.
		private async Task PumpAsync( CancellationToken stoppingToken )
		{
			while( !stoppingToken.IsCancellationRequested )
			{
				// Capture the signal before reading state so a change between the read and the
				// wait still interrupts the wait.
				Task stateChanged = _stateChanged.Task;

				bool atDetachedLimit = AtDetachedLimit();

				LogClaimingTransition( atDetachedLimit );

				if( IsPaused || atDetachedLimit )
				{
					await WaitForAsync( stateChanged, Timeout.InfiniteTimeSpan, stoppingToken )
						.ConfigureAwait( false );

					continue;
				}

				// Taken before the request is sent: the coordinator starts the lease no earlier, so the
				// worker's deadline measured from here cannot fall after the coordinator's (D29).
				long claimRequested = Stopwatch.GetTimestamp();

				( ClaimedJob job, bool refused ) = await ClaimAsync().ConfigureAwait( false );

				if( job is not null )
				{
					// One attended job at a time: the next claim waits for this one to finish or
					// be detached, and for its report to be settled. Polling while the claim is still
					// held would adopt this very run again (D20).
					bool recorded = await ProcessJobAsync( job, claimRequested, stoppingToken ).ConfigureAwait( false );

					// A recorded report frees the run, and more work may already be due: poll again
					// at once.
					if( recorded )
						continue;

					// Otherwise (abandoned, not held, dropped unconfirmed or refused, or failed for an
					// unusable claim) the schedule row may still name this worker as holder, and the
					// next claim would adopt the same run straight away (D20). Waiting PollInterval
					// bounds such self-adoption loops — a claim the worker cannot time, a proxy's 404
					// read as an unknown run — to one execution per PollInterval at worst. Like the
					// no-work wait, a state change (resume, a detached handler ending) or the host
					// stopping cuts it short; the signal is captured now, so only a change after
					// this run counts.
					await WaitForAsync( _stateChanged.Task, _options.PollInterval, stoppingToken ).ConfigureAwait( false );

					continue;
				}

				_logger.LogDebug( "Worker {Claimant}/{Worker} (job type {JobType}) found no due work.",
								_options.Claimant, _worker, _jobTypeFilter );

				// A refusal concerns the claimant's grants, or a security provider that is failing closed,
				// and neither recovers in a PollInterval: polling at the slower RefusedPollRetryInterval
				// keeps a refused worker from hammering the coordinator and its security backend while
				// still recovering on its own. Pause and resume, a detached handler ending, or the host
				// stopping cut either wait short.
				await WaitForAsync( stateChanged, refused ? _options.RefusedPollRetryInterval : _options.PollInterval,
									stoppingToken )
					.ConfigureAwait( false );
			}
		}

		// Polls the coordinator once: the job, or null when none was handed out (nothing due, or the
		// claim failed), and whether the claim was refused (NotAuthorized). A refusal holds and
		// executes nothing, so the worker keeps polling and recovers by itself once claims are
		// answered again. It shows as WorkerFaults.ClaimRefused for the whole episode, from the first
		// refusal until a claim is answered (with a job or with none), and is logged once per episode:
		// an Error on its first refusal, Information on its recovery, Debug in between. Any other
		// failure is not an answer, so it neither sets nor clears the fault.
		private async Task<(ClaimedJob Job, bool Refused)> ClaimAsync()
		{
			ClaimedJob job = null;
			bool answered = false;
			bool refused = false;

			try
			{
				job = await _coordinator.ClaimDueJobAsync( _options.Claimant, _worker, _options.JobType )
										.ConfigureAwait( false );

				answered = true;
			}
			catch( NotAuthorized exception )
			{
				refused = true;

				if( _fault is null )
					_logger.LogError( exception,
									"Worker {Claimant}/{Worker} (job type {JobType}) was refused a claim: grant {Operation} on " +
									"the execution coordinator to the claimant, or check its security provider. It retries " +
									"every {RetryInterval}, and claims once answered.",
									_options.Claimant, _worker, _jobTypeFilter, CoordinatorSecurityOperations.ClaimJobs,
									_options.RefusedPollRetryInterval );
				else
					_logger.LogDebug( exception, "Worker {Claimant}/{Worker} (job type {JobType}) is still refused claims.",
									_options.Claimant, _worker, _jobTypeFilter );
			}
			catch( Exception exception )
			{
				_logger.LogError( exception, "Worker {Claimant}/{Worker} (job type {JobType}) failed to claim a due job.",
								_options.Claimant, _worker, _jobTypeFilter );
			}

			if( answered && _fault is not null )
				_logger.LogInformation( "Worker {Claimant}/{Worker} (job type {JobType}) is no longer refused claims.",
										_options.Claimant, _worker, _jobTypeFilter );

			lock( _stateLock )
			{
				_lastPoll = _timeProvider.GetUtcNow().UtcDateTime;

				if( refused )
					_fault = WorkerFaults.ClaimRefused;
				else if( answered )
					_fault = null;
			}

			return ( job, refused );
		}

		// Whether MaxDetachedHandlers detached handlers are still running, so claiming is suspended.
		// A detached handler is still running and the worker cannot stop it; the limit stops stuck
		// handlers from piling up without bound. The count falls in AwaitDetachedAsync.
		private bool AtDetachedLimit() => Volatile.Read( ref _detachedJobs ) >= _options.MaxDetachedHandlers;

		// Logs the suspension of claiming once (an Error), and its resumption. Called every loop
		// iteration with the current state; only a change of state logs, so a long suspension is
		// one entry, not one per wake-up.
		private void LogClaimingTransition( bool suspended )
		{
			if( suspended == _claimingSuspended )
				return;

			_claimingSuspended = suspended;

			int detached = Volatile.Read( ref _detachedJobs );

			if( suspended )
				_logger.LogError(
						"Worker {Claimant}/{Worker} (job type {JobType}) stopped claiming: {Detached} detached handler(s) " +
						"still running, at the limit of {MaxDetachedHandlers}. Claiming resumes when one ends.",
						_options.Claimant, _worker, _jobTypeFilter, detached, _options.MaxDetachedHandlers );
			else
				_logger.LogInformation(
						"Worker {Claimant}/{Worker} (job type {JobType}) resumed claiming: {Detached} detached handler(s) still running.",
						_options.Claimant, _worker, _jobTypeFilter, detached );
		}

		// Waits until the signal completes, the delay elapses, or cancellation is requested,
		// whichever is first; Timeout.InfiniteTimeSpan removes the delay bound. Never throws on
		// cancellation: callers inspect their own state (the signal's task, the token) to see which
		// ended the wait.
		private static async Task WaitForAsync( Task signal, TimeSpan delay, CancellationToken cancellationToken )
		{
			using CancellationTokenSource delayCancellation =
				CancellationTokenSource.CreateLinkedTokenSource( cancellationToken );

			Task delayTask = Task.Delay( delay, delayCancellation.Token );

			await Task.WhenAny( signal, delayTask ).ConfigureAwait( false );

			// Release the timer when the signal won the race.
			await delayCancellation.CancelAsync().ConfigureAwait( false );

			try
			{
				await delayTask.ConfigureAwait( false );
			}
			catch( OperationCanceledException )
			{
				// Interrupted wait — expected.
			}
		}

		// Executes a claimed job under attendance, then reports its outcome, abandons it, or —
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
		private async Task<bool> ProcessJobAsync( ClaimedJob job, long claimRequested, CancellationToken stoppingToken )
		{
			Interlocked.Increment( ref _activeJobs );

			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();

				_logger.LogInformation(
						"Worker {Claimant}/{Worker} claimed run {Run} of schedule {Schedule} ({JobType}).",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType );

				// Parameters the worker cannot honour (they may come from a remote coordinator) fail the
				// run; they must not escape the pump. With no claim it can time, the failure is reported
				// once and never retried.
				RunReport unexecutable = Unexecutable( job );

				if( unexecutable is not null )
					return await ReportAsync( job, unexecutable, stopwatch, LeaseDeadline.Passed(),
											Stopwatch.GetTimestamp(), stoppingToken )
								.ConfigureAwait( false );

				LeaseDeadline lease = new( job.ClaimDuration, claimRequested );

				// The run's timeout counts from here (RunCancellation's timeout watcher starts now); it
				// also ends the window for retrying the report (ReportAsync).
				long handlerStarted = Stopwatch.GetTimestamp();

				RunCancellation cancellation = new(
						job.Timeout, stoppingToken, exception => LogCallbackFault( job, exception ) );

				Task<JobOutcome> run = StartHandler( job, cancellation.Token );

				// A snapshot: a handler completing between here and Detach below is still detached,
				// and reported as TIMED_OUT.
				bool finished = await ExecuteAttendedAsync( job, run, cancellation, lease ).ConfigureAwait( false );

				RunReport report = ClassifyOutcome( job, run, finished, cancellation );

				// The run's cancellation is the handler's until it ends: disposed here if it ended,
				// otherwise handed to Detach, which disposes it when the handler ends. Its reason flags
				// stay readable after disposal.
				if( finished )
					cancellation.Dispose();
				else
					Detach( job, run, cancellation, stopwatch );

				// A null report means abandoned (claim lost, or host stopping).
				if( report is not null )
					return await ReportAsync( job, report, stopwatch, lease, handlerStarted, stoppingToken )
								.ConfigureAwait( false );

				// The host is stopping and the claim was not lost: release it, so a peer adopts the run
				// without waiting out the lease (D27). Only a stopped handler's claim: a detached one
				// is still running, so its claim must lapse rather than let a peer adopt the run while
				// the old handler still works on it. A claim no longer held has nothing to release; a
				// lapsed one is left to its lease.
				if( finished && cancellation.HostStopping && !cancellation.ClaimLost )
					await TryAbandonClaimAsync( job ).ConfigureAwait( false );

				return false;
			}
			finally
			{
				Interlocked.Decrement( ref _activeJobs );
			}
		}

		// Runs the handler off the pump's thread (Task.Run): the factory and the handler's synchronous
		// prefix are third-party code, so one that blocks synchronously cannot stall the pump, the
		// heartbeat or the timeout. The token is the run's, not the worker's, so only a stop signal
		// of this run cancels it.
		private Task<JobOutcome> StartHandler( ClaimedJob job, CancellationToken cancellationToken ) =>
			Task.Run( () => RunHandlerAsync( job, cancellationToken ), CancellationToken.None );

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
		private async Task<bool> ExecuteAttendedAsync( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation,
														LeaseDeadline lease )
		{
			using CancellationTokenSource heartbeatStop = new();

			Task heartbeat = HeartbeatAsync( job, lease, cancellation.LoseClaim, heartbeatStop.Token );

			try
			{
				// Phase 1: until the handler finishes or the run is stopped (timeout, claim lost,
				// host stopping); no other limit.
				await WaitForAsync( run, Timeout.InfiniteTimeSpan, cancellation.Token ).ConfigureAwait( false );

				// Phase 2: stopped but still running; bounded by CancellationGrace, never cancelled.
				if( !run.IsCompleted )
					await WaitForAsync( run, _options.CancellationGrace, CancellationToken.None ).ConfigureAwait( false );

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

		// What to report for a run no longer attended; null abandons it without reporting.
		//
		// Precedence when several stop reasons coincide: timeout > claim lost / host stopping >
		// exception. A lost claim is never reported (a peer may already own the occurrence).
		private RunReport ClassifyOutcome( ClaimedJob job, Task<JobOutcome> run, bool finished, RunCancellation cancellation ) =>
			finished ? ClassifyFinished( job, run, cancellation ) : ClassifyDetached( job, cancellation );

		// The handler ended (normally, by exception, or by honouring cancellation); the run is
		// already complete, so GetResult does not block.
		private RunReport ClassifyFinished( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation )
		{
			RunReport report;

			// Catch order is the precedence: a timeout firing during a shutdown-initiated grace reports
			// TIMED_OUT, the same as when the handler ends within the grace. A cancellation exception
			// caused by claim loss or shutdown abandons; any other exception is a failed run.
			try
			{
				report = Completed( job, run.GetAwaiter().GetResult() );
			}
			catch( OperationCanceledException ) when( cancellation.TimedOut )
			{
				report = TimedOut( job, $"Execution exceeded the schedule timeout of {job.Timeout}." );
			}
			catch( OperationCanceledException ) when( cancellation.ClaimLost || cancellation.HostStopping )
			{
				LogAbandoned( job, cancellation, "" );

				return null;
			}
			catch( Exception exception )
			{
				report = Threw( job, exception );
			}

			// The handler completed, but not under the claim: even a clean outcome is dropped.
			if( cancellation.ClaimLost )
			{
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} completed run {Run} of schedule {Schedule} after losing its claim; " +
						"outcome not reported.",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule );

				return null;
			}

			return report;
		}

		// The handler was stopped and did not finish within the grace. Only a timeout is reported;
		// a lost claim or a stopping host abandons the run, as when the handler honours cancellation.
		private RunReport ClassifyDetached( ClaimedJob job, RunCancellation cancellation )
		{
			if( cancellation.TimedOut && !cancellation.ClaimLost )
				return TimedOut( job, $"Handler did not stop within {_options.CancellationGrace} of cancellation; detached." );

			LogAbandoned( job, cancellation, "; handler detached" );

			return null;
		}

		private RunReport Completed( ClaimedJob job, JobOutcome outcome )
		{
			outcome ??= new JobOutcome { Successful = false, Error = "Job handler returned no outcome." };

			if( !outcome.Successful )
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) was unsuccessful: {Error}",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
						job.Context.JobType, outcome.Error );

			return new RunReport( outcome.Successful ? RunResults.Succeeded : RunResults.Failed, outcome );
		}

		// The failed report for a claim the worker cannot execute safely, or null when it can: a
		// timeout no timer accepts, or a claim duration the worker cannot time. The claim duration
		// must be at least MinimumClaimDuration, so the window after LeaseTolerance leaves room for
		// renewals (a coordinator enforces a far larger minimum, but a remote one is not trusted;
		// an old coordinator sends none, i.e. zero), and within TimerBounds, or it could not be
		// waited on.
		private RunReport Unexecutable( ClaimedJob job )
		{
			string error;

			if( !TimerBounds.Contains( job.Timeout ) )
				error = $"Job timeout {job.Timeout} is outside the supported range (0 to {TimerBounds.Maximum}); " +
						"the job was not executed.";
			else if( job.ClaimDuration < MinimumClaimDuration || job.ClaimDuration > TimerBounds.Maximum )
				error = $"Claim duration {job.ClaimDuration} is outside the range the worker can time ({MinimumClaimDuration} " +
						$"to {TimerBounds.Maximum}); the job was not executed.";
			else
				return null;

			_logger.LogError(
					"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) not executed: {Error}",
					_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType, error );

			return new RunReport( RunResults.Failed, new JobOutcome { Successful = false, Error = error } );
		}

		private RunReport TimedOut( ClaimedJob job, string error )
		{
			_logger.LogWarning(
					"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) timed out after {Duration}: {Error}",
					_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
					job.Context.JobType, job.Timeout, error );

			return new RunReport( RunResults.TimedOut, new JobOutcome { Successful = false, Error = error } );
		}

		// Third-party handler code: any escaping exception is an unsuccessful outcome.
		private RunReport Threw( ClaimedJob job, Exception exception )
		{
			_logger.LogWarning( exception,
								"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) threw.",
								_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
								job.Context.JobType );

			return new RunReport( RunResults.Failed, new JobOutcome { Successful = false, Error = exception.ToString() } );
		}

		// Abandoning without reporting: the claim was lost (a peer may already own the occurrence) or
		// the host is stopping. At-least-once semantics re-execute the occurrence once the lease
		// expires.
		private void LogAbandoned( ClaimedJob job, RunCancellation cancellation, string detail ) =>
			_logger.LogWarning(
					"Worker {Claimant}/{Worker} abandoned run {Run} of schedule {Schedule}: {Reason}{Detail}.",
					_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
					cancellation.Loss switch
					{
						ClaimLoss.NotHeld => "claim no longer held",
						ClaimLoss.Lapsed => "claim lapsed unrenewed",
						_ => "host stopping"
					},
					detail );

		// Best effort: releases the claim so a peer adopts the run without waiting for the lease,
		// bounded by AbandonTimeout. A failure only means the lease lapses on its own.
		private async Task TryAbandonClaimAsync( ClaimedJob job )
		{
			try
			{
				bool released = await _coordinator.AbandonClaimAsync( job.Context.Run, _options.Claimant, _worker )
												.WaitAsync( _options.AbandonTimeout ).ConfigureAwait( false );

				_logger.LogInformation(
						"Worker {Claimant}/{Worker} released its claim on run {Run} of schedule {Schedule}: {Released}.",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
						released ? "peers may adopt it now" : "it was no longer held" );
			}
			catch( Exception exception )
			{
				_logger.LogWarning( exception,
									"Worker {Claimant}/{Worker} could not release its claim on run {Run} of schedule {Schedule}; " +
									"it lapses with the lease.",
									_options.Claimant, _worker, job.Context.Run, job.Context.Schedule );
			}
		}

		// Leaves a handler that ignored cancellation to end on its own. Nothing bounds how long that
		// takes; MaxDetachedHandlers bounds how many may accumulate. Ownership moves to
		// AwaitDetachedAsync: the handler itself is disposed by RunHandlerAsync when it ends, and the
		// run's RunCancellation (whose token the handler may still hold) is disposed by
		// AwaitDetachedAsync, never earlier.
		private void Detach( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation, Stopwatch stopwatch )
		{
			int detached = Interlocked.Increment( ref _detachedJobs );

			_logger.LogWarning(
					"Worker {Claimant}/{Worker} detached the handler of run {Run} of schedule {Schedule} ({JobType}): " +
					"it did not stop within {CancellationGrace} of cancellation. {Detached} handler(s) detached.",
					_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType,
					_options.CancellationGrace, detached );

			_ = AwaitDetachedAsync( job, run, cancellation, stopwatch );
		}

		// Observes the detached handler until it ends, however long that takes, only to log it and
		// release what it held. Its outcome is never reported: the run was already reported
		// (TIMED_OUT) or abandoned when it was detached.
		private async Task AwaitDetachedAsync( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation,
												Stopwatch stopwatch )
		{
			try
			{
				await run.ConfigureAwait( false );

				_logger.LogWarning(
						"Worker {Claimant}/{Worker} detached handler of run {Run} of schedule {Schedule} ({JobType}) " +
						"ended after {Duration}.",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType,
						stopwatch.Elapsed );
			}
			catch( Exception exception )
			{
				_logger.LogWarning( exception,
									"Worker {Claimant}/{Worker} detached handler of run {Run} of schedule {Schedule} ({JobType}) " +
									"ended after {Duration} with a fault.",
									_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType,
									stopwatch.Elapsed );
			}
			finally
			{
				// The handler has ended, so nothing can still observe the token.
				cancellation.Dispose();

				Interlocked.Decrement( ref _detachedJobs );

				// Wakes a pump suspended at the detached limit so it can claim again.
				SignalStateChanged();
			}
		}

		// Reports the run's outcome (D29), and returns whether it was recorded. What the coordinator
		// answers decides what follows:
		// - recorded: done;
		// - not held (ClaimNotHeldException, UnknownRunException): the run was adopted, finished or
		//   deleted; the outcome is dropped;
		// - anything else, a refusal (NotAuthorized) included: retried every PollInterval within the
		//   retry window, then dropped with an Error. A refusal is retried rather than acted on,
		//   because a fail-closed security provider refuses during an outage exactly as a missing
		//   grant does; no claim is released and the worker never pauses.
		//
		// The retry window ends at the earlier of two deadlines, past either of which an outcome is
		// no longer worth delivering:
		// - the lease deadline: past it a peer may adopt the run, and the claim is as good as lost;
		// - the run's timeout deadline (handlerStarted + ClaimedJob.Timeout): past it the job counts
		//   as failed, and a late outcome would contradict that. A TIMED_OUT report therefore gets
		//   exactly one attempt, as does the report of a claim the worker could not time.
		// The pump claims nothing while retrying, then waits PollInterval after a dropped report, so
		// a report that never gets through costs one execution per window plus PollInterval.
		//
		// Retries have the heartbeat's shape. When the window's remainder is no longer than
		// PollInterval, the wait is the whole remainder and the attempt after it is the last: the
		// deadline is not re-read, since a timer may fire a little before the Stopwatch deadline and
		// the remainder would then read tiny-positive and spin. The first attempt is made even past
		// the window: the coordinator accepts a lapsed holder's report until a peer adopts the run
		// (D23). The host stopping ends the retries at once; a single attempt in flight is bounded
		// only by the coordinator's own call (the transport's and the store's timeouts), which a
		// host's shutdown budget must allow for.
		private async Task<bool> ReportAsync( ClaimedJob job, RunReport report, Stopwatch stopwatch, LeaseDeadline lease,
											long handlerStarted, CancellationToken stoppingToken )
		{
			bool lastAttempt = false;

			while( true )
			{
				Exception unconfirmed;

				try
				{
					await _coordinator.ReportCompletionAsync(
										job.Context.Run, _options.Claimant, _worker, report.Result, report.Outcome )
									.ConfigureAwait( false );

					_logger.LogInformation(
							"Worker {Claimant}/{Worker} finished run {Run} of schedule {Schedule} ({JobType}) " +
							"with result {Result} in {Duration}.",
							_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
							job.Context.JobType, report.Result, stopwatch.Elapsed );

					return true;
				}
				catch( Exception exception ) when( exception is ClaimNotHeldException or UnknownRunException )
				{
					_logger.LogWarning( exception,
										"Worker {Claimant}/{Worker} no longer holds run {Run} of schedule {Schedule}; " +
										"its {Result} outcome was not recorded.",
										_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, report.Result );

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

				TimeSpan untilTimeout = job.Timeout - Stopwatch.GetElapsedTime( handlerStarted );
				TimeSpan untilLapse = lease.Remaining;
				TimeSpan remaining = untilTimeout < untilLapse ? untilTimeout : untilLapse;

				if( !lastAttempt && remaining > TimeSpan.Zero && !stoppingToken.IsCancellationRequested )
				{
					lastAttempt = remaining <= _options.PollInterval;

					_logger.LogWarning( unconfirmed,
										"Worker {Claimant}/{Worker} failed to report completion of run {Run} of schedule " +
										"{Schedule}; retrying for up to {Remaining}.{Refusal}",
										_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, remaining,
										refusal );

					// Bounded by PollInterval or, for the last attempt, the window's remainder; the host
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
								"before {Reason}; its {Result} outcome was dropped and the run will be executed again.{Refusal}",
								_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
								stoppingToken.IsCancellationRequested ? "the host stopped"
								: untilTimeout < untilLapse ? "the run's timeout elapsed"
								: "its claim lapsed",
								report.Result, refusal );

				return false;
			}
		}

		// Creates the run's handler, executes it and disposes it once it ends — attended or
		// detached, so exactly once. Missing or failing factories are materialized as unsuccessful
		// outcomes; an exception escaping the handler propagates to the caller's classification.
		// Disposal failures never change the outcome.
		private async Task<JobOutcome> RunHandlerAsync( ClaimedJob job, CancellationToken cancellationToken )
		{
			ScheduledJobContext context = job.Context;

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
				return await handler.ExecuteAsync( context, job.Timeout, cancellationToken ).ConfigureAwait( false );
			}
			finally
			{
				await DisposeHandlerAsync( handler, context ).ConfigureAwait( false );
			}
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
		private async Task HeartbeatAsync( ClaimedJob job, LeaseDeadline lease, Action<ClaimLoss> loseClaim,
											CancellationToken stopToken )
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
						LoseLapsedClaim( job, refused, loseClaim );

						return;
					}

					long renewalRequested = Stopwatch.GetTimestamp();

					Task<bool> renewal = null;
					bool renewed;

					try
					{
						renewal = _coordinator.RenewClaimAsync( job.Context.Run, _options.Claimant, _worker );

						renewed = await renewal.WaitAsync( remaining, stopToken ).ConfigureAwait( false );
					}
					catch( OperationCanceledException ) when( stopToken.IsCancellationRequested )
					{
						return;
					}
					catch( TimeoutException ) when( renewal is { IsCompleted: false } )
					{
						// Still pending at the deadline: the lapse itself (see above).
						LoseLapsedClaim( job, refused, loseClaim );

						return;
					}
					catch( NotAuthorized exception )
					{
						refused = true;

						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} was refused the renewal of its claim on run {Run} of " +
											"schedule {Schedule} (grant {Operation} to the claimant, or check its security " +
											"provider); retrying while the claim stands ({Remaining} left).",
											_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
											CoordinatorSecurityOperations.RenewClaim, lease.Remaining );

						continue;
					}
					catch( Exception exception )
					{
						refused = false;

						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} failed to renew its claim on run {Run} of schedule " +
											"{Schedule}; retrying while the claim stands ({Remaining} left).",
											_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
											lease.Remaining );

						continue;
					}

					if( !renewed )
					{
						_logger.LogWarning(
								"Worker {Claimant}/{Worker} no longer holds its claim on run {Run} of schedule {Schedule}.",
								_options.Claimant, _worker, job.Context.Run, job.Context.Schedule );

						loseClaim( ClaimLoss.NotHeld );

						return;
					}

					lease.Renewed( renewalRequested );

					refused = false;

					_logger.LogDebug( "Worker {Claimant}/{Worker} renewed claim of run {Run}.",
									_options.Claimant, _worker, job.Context.Run );
				}
			}
			catch( OperationCanceledException ) when( stopToken.IsCancellationRequested )
			{
				// Heartbeat stopped with the job — expected.
			}
		}

		// A claim lost to refused renewals is an Error naming the grant: with RENEW_CLAIM truly
		// missing, every run longer than a beat ends this way and is executed again, so it is what an
		// operator must alert on. Lapses for any other reason are Warnings.
		private void LoseLapsedClaim( ClaimedJob job, bool refused, Action<ClaimLoss> loseClaim )
		{
			if( refused )
				_logger.LogError(
						"Worker {Claimant}/{Worker} lost its claim on run {Run} of schedule {Schedule}: the coordinator " +
						"refused its renewals for the whole claim duration of {ClaimDuration}. Grant {Operation} to " +
						"the claimant, or check its security provider; the run will be executed again.",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.ClaimDuration,
						CoordinatorSecurityOperations.RenewClaim );
			else
				_logger.LogWarning(
						"Worker {Claimant}/{Worker} could not renew its claim on run {Run} of schedule {Schedule} " +
						"within its claim duration of {ClaimDuration}; the claim is treated as lost.",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.ClaimDuration );

			loseClaim( ClaimLoss.Lapsed );
		}

		private void LogCallbackFault( ClaimedJob job, Exception exception ) =>
			_logger.LogWarning( exception,
								"Worker {Claimant}/{Worker} run {Run}: a cancellation callback threw while the run was being cancelled.",
								_options.Claimant, _worker, job.Context.Run );

		// What the worker reports for a run.
		private sealed record RunReport( string Result, JobOutcome Outcome );

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
				: this( claimRequested, claimDuration - LeaseTolerance )
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
	}
}
