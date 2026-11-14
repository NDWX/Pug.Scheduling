using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
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
	/// The worker attends one job at a time; at <see cref="WorkerOptions.MaxDetachedHandlers"/>
	/// detached handlers it stops claiming until one ends.
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

		private readonly object _stateLock = new();
		private bool _paused;
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

		private async Task PumpAsync( CancellationToken stoppingToken )
		{
			while( !stoppingToken.IsCancellationRequested )
			{
				// Capture the signal before reading state so a change between the read and the
				// wait still interrupts the wait.
				Task stateChanged = _stateChanged.Task;

				if( IsPaused || ClaimingSuspended() )
				{
					await WaitForAsync( stateChanged, Timeout.InfiniteTimeSpan, stoppingToken )
						.ConfigureAwait( false );

					continue;
				}

				ClaimedJob job = await ClaimAsync().ConfigureAwait( false );

				if( job is not null )
				{
					// One attended job at a time: the next claim waits for this one to finish or
					// be detached.
					await ProcessJobAsync( job, stoppingToken ).ConfigureAwait( false );

					// More work may already be due; poll again immediately.
					continue;
				}

				_logger.LogDebug( "Worker {Claimant}/{Worker} (job type {JobType}) found no due work.",
								_options.Claimant, _worker, _jobTypeFilter );

				await WaitForAsync( stateChanged, _options.PollInterval, stoppingToken ).ConfigureAwait( false );
			}
		}

		// Polls the coordinator once; a failed claim is logged and treated as no due work.
		private async Task<ClaimedJob> ClaimAsync()
		{
			ClaimedJob job = null;

			try
			{
				job = await _coordinator.ClaimDueJobAsync( _options.Claimant, _worker, _options.JobType )
										.ConfigureAwait( false );
			}
			catch( Exception exception )
			{
				_logger.LogError( exception, "Worker {Claimant}/{Worker} (job type {JobType}) failed to claim a due job.",
								_options.Claimant, _worker, _jobTypeFilter );
			}

			lock( _stateLock )
				_lastPoll = _timeProvider.GetUtcNow().UtcDateTime;

			return job;
		}

		// Whether claiming is suspended because MaxDetachedHandlers detached handlers are still
		// running (any detached handler when the bound is 0). Logs the suspension once, and the
		// resumption.
		private bool ClaimingSuspended()
		{
			int detached = Volatile.Read( ref _detachedJobs );

			bool suspend = detached > 0 && detached >= _options.MaxDetachedHandlers;

			if( suspend && !_claimingSuspended )
				_logger.LogError(
						"Worker {Claimant}/{Worker} (job type {JobType}) stopped claiming: {Detached} detached handler(s) " +
						"still running, at the limit of {MaxDetachedHandlers}. Claiming resumes when one ends.",
						_options.Claimant, _worker, _jobTypeFilter, detached, _options.MaxDetachedHandlers );
			else if( !suspend && _claimingSuspended )
				_logger.LogInformation(
						"Worker {Claimant}/{Worker} (job type {JobType}) resumed claiming: {Detached} detached handler(s) still running.",
						_options.Claimant, _worker, _jobTypeFilter, detached );

			_claimingSuspended = suspend;

			return suspend;
		}

		// Waits until the signal completes, the delay elapses, or cancellation is requested.
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
		private async Task ProcessJobAsync( ClaimedJob job, CancellationToken stoppingToken )
		{
			Interlocked.Increment( ref _activeJobs );

			try
			{
				Stopwatch stopwatch = Stopwatch.StartNew();

				_logger.LogInformation(
						"Worker {Claimant}/{Worker} claimed run {Run} of schedule {Schedule} ({JobType}).",
						_options.Claimant, _worker, job.Context.Run, job.Context.Schedule, job.Context.JobType );

				RunCancellation cancellation = new( job.Timeout, stoppingToken );

				Task<JobOutcome> run = StartHandler( job, cancellation.Token );

				bool finished = await ExecuteAttendedAsync( job, run, cancellation ).ConfigureAwait( false );

				RunReport report = ClassifyOutcome( job, run, finished, cancellation );

				// The run's cancellation is the handler's until it ends.
				if( finished )
					cancellation.Dispose();
				else
					Detach( job, run, cancellation, stopwatch );

				if( report is not null )
					await ReportAsync( job, report, stopwatch ).ConfigureAwait( false );
			}
			finally
			{
				Interlocked.Decrement( ref _activeJobs );
			}
		}

		// Runs the handler off the pump's thread, so that a handler blocking synchronously cannot
		// stall the worker either.
		private Task<JobOutcome> StartHandler( ClaimedJob job, CancellationToken cancellationToken ) =>
			Task.Run( () => RunHandlerAsync( job, cancellationToken ), CancellationToken.None );

		// Heartbeats the claim while waiting for the handler to finish or a stop signal (timeout,
		// claim lost, host stopping). A stopped handler has been cancelled and is given
		// CancellationGrace to finish. Returns whether it finished; the heartbeat stops either way.
		private async Task<bool> ExecuteAttendedAsync( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation )
		{
			using CancellationTokenSource heartbeatStop = new();

			Task heartbeat = HeartbeatAsync( job, cancellation.LoseClaim, heartbeatStop.Token );

			try
			{
				await WaitForAsync( run, Timeout.InfiniteTimeSpan, cancellation.Token ).ConfigureAwait( false );

				if( !run.IsCompleted )
					await WaitForAsync( run, _options.CancellationGrace, CancellationToken.None ).ConfigureAwait( false );

				return run.IsCompleted;
			}
			finally
			{
				await heartbeatStop.CancelAsync().ConfigureAwait( false );

				await heartbeat.ConfigureAwait( false );
			}
		}

		// What to report for a run no longer attended; null abandons it without reporting.
		private RunReport ClassifyOutcome( ClaimedJob job, Task<JobOutcome> run, bool finished, RunCancellation cancellation ) =>
			finished ? ClassifyFinished( job, run, cancellation ) : ClassifyDetached( job, cancellation );

		private RunReport ClassifyFinished( ClaimedJob job, Task<JobOutcome> run, RunCancellation cancellation )
		{
			RunReport report;

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

		// Abandoning without reporting: the claim lapsed (a peer may already own the occurrence) or
		// the host is stopping. At-least-once semantics re-execute the occurrence once the lease
		// expires.
		private void LogAbandoned( ClaimedJob job, RunCancellation cancellation, string detail ) =>
			_logger.LogWarning(
					"Worker {Claimant}/{Worker} abandoned run {Run} of schedule {Schedule}: {Reason}{Detail}.",
					_options.Claimant, _worker, job.Context.Run, job.Context.Schedule,
					cancellation.ClaimLost ? "claim lost" : "host stopping", detail );

		// Leaves a handler that ignored cancellation to end on its own. Its run's cancellation and
		// the handler itself (disposed by RunHandlerAsync) are released when it ends.
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
				cancellation.Dispose();

				Interlocked.Decrement( ref _detachedJobs );

				SignalStateChanged();
			}
		}

		private async Task ReportAsync( ClaimedJob job, RunReport report, Stopwatch stopwatch )
		{
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
			}
			catch( Exception exception )
			{
				_logger.LogError( exception,
								"Worker {Claimant}/{Worker} failed to report completion of run {Run} of schedule {Schedule}.",
								_options.Claimant, _worker, job.Context.Run, job.Context.Schedule );
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

		// Renews the job's claim on a fixed cadence until stopped. Transient renewal errors are
		// tolerated (the lease outlives one missed heartbeat); a definitive 'no longer held'
		// answer abandons the run.
		private async Task HeartbeatAsync( ClaimedJob job, Action onClaimLost, CancellationToken stopToken )
		{
			try
			{
				while( !stopToken.IsCancellationRequested )
				{
					await Task.Delay( _options.HeartbeatInterval, stopToken ).ConfigureAwait( false );

					bool renewed;

					try
					{
						renewed = await _coordinator.RenewClaimAsync( job.Context.Run, _options.Claimant, _worker )
													.ConfigureAwait( false );
					}
					catch( Exception exception )
					{
						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} failed to renew claim of run {Run}; will retry.",
											_options.Claimant, _worker, job.Context.Run );

						continue;
					}

					if( renewed )
						_logger.LogDebug( "Worker {Claimant}/{Worker} renewed claim of run {Run}.",
										_options.Claimant, _worker, job.Context.Run );

					if( !renewed )
					{
						onClaimLost();

						return;
					}
				}
			}
			catch( OperationCanceledException )
			{
				// Heartbeat stopped with the job — expected.
			}
		}

		// What the worker reports for a run.
		private sealed record RunReport( string Result, JobOutcome Outcome );

		// A run's stop signals — its timeout, the loss of its claim, and host shutdown — linked
		// into the one token its handler observes. Owned by the handler until it ends: a detached
		// handler may still hold the token.
		private sealed class RunCancellation : IDisposable
		{
			private readonly CancellationTokenSource _timeout;
			private readonly CancellationTokenSource _claimLost = new();
			private readonly CancellationTokenSource _execution;
			private readonly CancellationToken _stopping;

			public RunCancellation( TimeSpan timeout, CancellationToken stoppingToken )
			{
				_timeout = new CancellationTokenSource( timeout );
				_stopping = stoppingToken;

				_execution = CancellationTokenSource.CreateLinkedTokenSource(
						stoppingToken, _timeout.Token, _claimLost.Token );
			}

			public CancellationToken Token => _execution.Token;

			public bool TimedOut => _timeout.IsCancellationRequested;

			public bool ClaimLost => _claimLost.IsCancellationRequested;

			public bool HostStopping => _stopping.IsCancellationRequested;

			public void LoseClaim() => _claimLost.Cancel();

			public void Dispose()
			{
				_execution.Dispose();
				_claimLost.Dispose();
				_timeout.Dispose();
			}
		}
	}
}
