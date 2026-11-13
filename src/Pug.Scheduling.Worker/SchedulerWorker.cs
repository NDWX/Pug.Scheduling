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
	/// outcome is materialized as an unsuccessful <see cref="JobOutcome"/>, cancellation is not
	/// assumed to be honored (the claim's lease expiry bounds runaways), and no failure of one
	/// job ever stops the poll loop.
	/// </summary>
	public sealed class SchedulerWorker : BackgroundService, ISchedulerWorker
	{
		// The worker's number within its claimant group. One worker per claimant until worker
		// groups arrive; the coordinator holds this worker's claims as "Claimant/1".
		private const int WorkerNumber = 1;

		private readonly IExecutionCoordinator _coordinator;
		private readonly IScheduledJobHandlerFactoryRegistry _handlerFactories;
		private readonly WorkerOptions _options;
		private readonly ILogger<SchedulerWorker> _logger;

		private readonly object _stateLock = new();
		private bool _paused;
		private DateTime? _lastPoll;
		private int _activeJobs;

		// Completed (and replaced) on pause/resume so the loop's waits are interrupted
		// immediately (FetchWorker precedent).
		private TaskCompletionSource _stateChanged = NewSignal();

		public SchedulerWorker( IExecutionCoordinator coordinator,
								IScheduledJobHandlerFactoryRegistry handlerFactories,
								WorkerOptions options,
								ILogger<SchedulerWorker> logger )
		{
			_coordinator = coordinator ?? throw new ArgumentNullException( nameof(coordinator) );
			_handlerFactories = handlerFactories ?? throw new ArgumentNullException( nameof(handlerFactories) );
			_options = options ?? throw new ArgumentNullException( nameof(options) );

			if( string.IsNullOrWhiteSpace( _options.Claimant ) )
				throw new ArgumentException( "Worker claimant name is required.", nameof(options) );

			_logger = logger ?? throw new ArgumentNullException( nameof(logger) );
		}

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
					Paused = _paused,
					LastPoll = _lastPoll,
					ActiveJobs = _activeJobs
				};
		}

		public void Pause()
		{
			lock( _stateLock )
				_paused = true;

			_logger.LogInformation( "Worker {Claimant}/{Worker} paused.", _options.Claimant, WorkerNumber );

			SignalStateChanged();
		}

		public void Resume()
		{
			lock( _stateLock )
				_paused = false;

			_logger.LogInformation( "Worker {Claimant}/{Worker} resumed.", _options.Claimant, WorkerNumber );

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
			_logger.LogInformation( "Worker {Claimant}/{Worker} started.", _options.Claimant, WorkerNumber );

			try
			{
				await PumpAsync( stoppingToken ).ConfigureAwait( false );
			}
			finally
			{
				_logger.LogInformation( "Worker {Claimant}/{Worker} stopping.", _options.Claimant, WorkerNumber );
			}
		}

		private async Task PumpAsync( CancellationToken stoppingToken )
		{
			while( !stoppingToken.IsCancellationRequested )
			{
				// Capture the signal before reading state so a change between the read and the
				// wait still interrupts the wait.
				Task stateChanged = _stateChanged.Task;

				if( IsPaused )
				{
					await WaitForAsync( stateChanged, Timeout.InfiniteTimeSpan, stoppingToken )
						.ConfigureAwait( false );

					continue;
				}

				ClaimedJob job = null;

				try
				{
					job = await _coordinator.ClaimDueJobAsync( _options.Claimant, WorkerNumber, null )
											.ConfigureAwait( false );
				}
				catch( Exception exception )
				{
					_logger.LogError( exception, "Worker {Claimant}/{Worker} failed to claim a due job.",
									_options.Claimant, WorkerNumber );
				}

				lock( _stateLock )
					_lastPoll = DateTime.UtcNow;

				if( job is not null )
				{
					// One job at a time: the next claim waits for this one to finish.
					await ProcessJobAsync( job, stoppingToken ).ConfigureAwait( false );

					// More work may already be due; poll again immediately.
					continue;
				}

				_logger.LogDebug( "Worker {Claimant}/{Worker} found no due work.", _options.Claimant, WorkerNumber );

				await WaitForAsync( stateChanged, _options.PollInterval, stoppingToken ).ConfigureAwait( false );
			}
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

		private async Task ProcessJobAsync( ClaimedJob job, CancellationToken stoppingToken )
		{
			Interlocked.Increment( ref _activeJobs );

			Stopwatch stopwatch = Stopwatch.StartNew();

			_logger.LogInformation(
					"Worker {Claimant}/{Worker} claimed run {Run} of schedule {Schedule} ({JobType}).",
					_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule, job.Context.JobType );

			try
			{
				using CancellationTokenSource timeout = new( job.Timeout );
				using CancellationTokenSource abandoned = new();

				using CancellationTokenSource execution =
					CancellationTokenSource.CreateLinkedTokenSource(
							stoppingToken, timeout.Token, abandoned.Token );

				using CancellationTokenSource heartbeatStop =
					CancellationTokenSource.CreateLinkedTokenSource( execution.Token );

				bool claimLost = false;

				Task heartbeat = HeartbeatAsync(
						job,
						onClaimLost: () =>
						{
							claimLost = true;
							abandoned.Cancel();
						},
						heartbeatStop.Token );

				string result;
				JobOutcome outcome;

				try
				{
					outcome = await RunHandlerAsync( job, execution.Token ).ConfigureAwait( false );

					outcome ??= new JobOutcome
					{
						Successful = false,
						Error = "Job handler returned no outcome."
					};

					result = outcome.Successful ? RunResults.Succeeded : RunResults.Failed;

					if( !outcome.Successful )
						_logger.LogWarning(
								"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) was unsuccessful: {Error}",
								_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule,
								job.Context.JobType, outcome.Error );
				}
				catch( OperationCanceledException ) when( timeout.IsCancellationRequested )
				{
					result = RunResults.TimedOut;

					_logger.LogWarning(
							"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) timed out after {Duration}.",
							_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule,
							job.Context.JobType, job.Timeout );

					outcome = new JobOutcome
					{
						Successful = false,
						Error = $"Execution exceeded the schedule timeout of {job.Timeout}."
					};
				}
				catch( OperationCanceledException ) when( claimLost || stoppingToken.IsCancellationRequested )
				{
					// Abandon without reporting: the claim lapsed (a peer may already own the
					// occurrence) or the host is stopping. At-least-once semantics re-execute
					// the occurrence once the lease expires.
					_logger.LogWarning(
							"Worker {Claimant}/{Worker} abandoned run {Run} of schedule {Schedule}: {Reason}.",
							_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule,
							claimLost ? "claim lost" : "host stopping" );

					return;
				}
				catch( Exception exception )
				{
					// Third-party handler code: any escaping exception is an unsuccessful outcome.
					result = RunResults.Failed;

					outcome = new JobOutcome { Successful = false, Error = exception.ToString() };

					_logger.LogWarning( exception,
										"Worker {Claimant}/{Worker} run {Run} of schedule {Schedule} ({JobType}) threw.",
										_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule,
										job.Context.JobType );
				}
				finally
				{
					await heartbeatStop.CancelAsync().ConfigureAwait( false );

					await heartbeat.ConfigureAwait( false );
				}

				if( claimLost )
				{
					_logger.LogWarning(
							"Worker {Claimant}/{Worker} completed run {Run} of schedule {Schedule} after losing its claim; " +
							"outcome not reported.",
							_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule );

					return;
				}

				try
				{
					await _coordinator.ReportCompletionAsync(
										job.Context.Run, _options.Claimant, WorkerNumber, result, outcome )
									.ConfigureAwait( false );

					_logger.LogInformation(
							"Worker {Claimant}/{Worker} finished run {Run} of schedule {Schedule} ({JobType}) " +
							"with result {Result} in {Duration}.",
							_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule,
							job.Context.JobType, result, stopwatch.Elapsed );
				}
				catch( Exception exception )
				{
					_logger.LogError( exception,
									"Worker {Claimant}/{Worker} failed to report completion of run {Run} of schedule {Schedule}.",
									_options.Claimant, WorkerNumber, job.Context.Run, job.Context.Schedule );
				}
			}
			finally
			{
				Interlocked.Decrement( ref _activeJobs );
			}
		}

		// Creates the run's handler, executes it and disposes it. Missing or failing factories are
		// materialized as unsuccessful outcomes; an exception escaping the handler propagates to
		// the caller's classification. Disposal failures never change the outcome.
		private async Task<JobOutcome> RunHandlerAsync( ClaimedJob job, CancellationToken cancellationToken )
		{
			ScheduledJobContext context = job.Context;

			IScheduledJobHandlerFactory factory = _handlerFactories.GetFactory( context.JobType );

			if( factory is null )
			{
				_logger.LogError(
						"Worker {Claimant}/{Worker} has no handler registered for job type {JobType} (run {Run} of schedule {Schedule}).",
						_options.Claimant, WorkerNumber, context.JobType, context.Run, context.Schedule );

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
								_options.Claimant, WorkerNumber, context.JobType, context.Run, context.Schedule );

				return new JobOutcome { Successful = false, Error = exception.ToString() };
			}

			if( handler is null )
			{
				_logger.LogError(
						"Worker {Claimant}/{Worker} handler factory for job type {JobType} returned no handler (run {Run} of schedule {Schedule}).",
						_options.Claimant, WorkerNumber, context.JobType, context.Run, context.Schedule );

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
									_options.Claimant, WorkerNumber, context.Run, context.Schedule, context.JobType );
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
						renewed = await _coordinator.RenewClaimAsync( job.Context.Run, _options.Claimant, WorkerNumber )
													.ConfigureAwait( false );
					}
					catch( Exception exception )
					{
						_logger.LogWarning( exception,
											"Worker {Claimant}/{Worker} failed to renew claim of run {Run}; will retry.",
											_options.Claimant, WorkerNumber, job.Context.Run );

						continue;
					}

					if( renewed )
						_logger.LogDebug( "Worker {Claimant}/{Worker} renewed claim of run {Run}.",
										_options.Claimant, WorkerNumber, job.Context.Run );

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
	}
}
