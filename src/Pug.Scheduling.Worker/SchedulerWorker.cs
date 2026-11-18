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
	/// against a remote coordinator transport, unchanged. The worker is the pump: it claims, pauses,
	/// and counts; one claimed job's execution, heartbeat, report and replay belong to the internal
	/// Work class.
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
	///
	/// A successful outcome the worker could not get recorded is remembered in its
	/// <see cref="UnrecordedSuccesses"/> store; when the occurrence is claimed again, the outcome is
	/// replayed instead of running the handler a second time.
	/// </summary>
	public sealed class SchedulerWorker : BackgroundService, ISchedulerWorker
	{
		private readonly IExecutionCoordinator _coordinator;
		private readonly IScheduledJobHandlerFactoryRegistry _handlerFactories;
		private readonly WorkerOptions _options;
		private readonly int _worker;
		private readonly TimeProvider _timeProvider;
		private readonly ILogger<SchedulerWorker> _logger;

		// Successful outcomes this worker, or a peer sharing the store, could not get recorded;
		// replayed when their occurrence is claimed again (D29).
		private readonly UnrecordedSuccesses _unrecordedSuccesses;

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
		/// <param name="unrecordedSuccesses">
		/// Where the worker remembers successful outcomes it could not get recorded, to replay them
		/// when their occurrence is claimed again. Share one store among every worker of the host on
		/// the same coordinator (AddSchedulingWorkers does); null gives the worker a store of its own.
		/// </param>
		public SchedulerWorker( IExecutionCoordinator coordinator,
								IScheduledJobHandlerFactoryRegistry handlerFactories,
								WorkerOptions options,
								int number,
								TimeProvider timeProvider,
								ILogger<SchedulerWorker> logger,
								UnrecordedSuccesses unrecordedSuccesses = null )
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
			_unrecordedSuccesses = unrecordedSuccesses ?? new UnrecordedSuccesses( _timeProvider );
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

		// Waits until the signal completes, the delay elapses, or cancellation is requested,
		// whichever is first; Timeout.InfiniteTimeSpan removes the delay bound. Never throws on
		// cancellation: callers inspect their own state (the signal's task, the token) to see which
		// ended the wait. Internal: Work waits on its handler with it too.
		internal static async Task WaitForAsync( Task signal, TimeSpan delay, CancellationToken cancellationToken )
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

		// Whether MaxDetachedHandlers detached handlers are still running, so claiming is suspended.
		// A detached handler is still running and the worker cannot stop it; the limit stops stuck
		// handlers from piling up without bound. The count falls when a detached handler ends (Work's
		// onDetachedEnded callback, supplied in PumpAsync).
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
					// Work executes it, from claim to settled report; the worker only counts it.
					Interlocked.Increment( ref _activeJobs );

					bool recorded;

					try
					{
						Work work = new( _coordinator, _handlerFactories, _options, _worker, _unrecordedSuccesses, _logger,
										job, claimRequested,
										// A handler detached: counted; the new count goes into Work's detach Warning.
										() => Interlocked.Increment( ref _detachedJobs ),
										// A detached handler ended: uncounted, and a pump suspended at the limit woken.
										() => { Interlocked.Decrement( ref _detachedJobs ); SignalStateChanged(); } );

						recorded = await work.ExecuteAsync( stoppingToken ).ConfigureAwait( false );
					}
					finally
					{
						Interlocked.Decrement( ref _activeJobs );
					}

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

				if( !refused )
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
	}
}
