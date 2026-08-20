using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker
{
	/// <summary>
	/// Execution pump: polls <see cref="IExecutionCoordinator"/> for due jobs, dispatches each
	/// through <see cref="IScheduledJobDispatcher"/> with timeout enforcement and claim
	/// heartbeats, and reports outcomes back. The worker knows nothing of schedules, stores, or
	/// leases beyond the coordinator surface — it can be hosted in-process with the scheduler or
	/// against a remote coordinator transport, unchanged.
	///
	/// Dispatchers and handlers are treated as third-party code: any escaping exception or null
	/// outcome is materialized as an unsuccessful <see cref="JobOutcome"/>, cancellation is not
	/// assumed to be honored (the claim's lease expiry bounds runaways), and no failure of one
	/// job ever stops the poll loop.
	/// </summary>
	public sealed class SchedulerWorker : BackgroundService, ISchedulerWorker
	{
		private readonly IExecutionCoordinator _coordinator;
		private readonly IScheduledJobDispatcher _dispatcher;
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
								IScheduledJobDispatcher dispatcher,
								WorkerOptions options,
								ILogger<SchedulerWorker> logger )
		{
			_coordinator = coordinator ?? throw new ArgumentNullException( nameof(coordinator) );
			_dispatcher = dispatcher ?? throw new ArgumentNullException( nameof(dispatcher) );
			_options = options ?? throw new ArgumentNullException( nameof(options) );

			if( string.IsNullOrWhiteSpace( _options.Claimant ) )
				throw new ArgumentException( "Worker claimant name is required.", nameof(options) );

			if( _options.MaximumConcurrentJobs < 1 )
				throw new ArgumentException( "At least one concurrent job is required.", nameof(options) );

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

			SignalStateChanged();
		}

		public void Resume()
		{
			lock( _stateLock )
				_paused = false;

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

				List<ClaimedJob> jobs = new();

				try
				{
					jobs.AddRange(
							await _coordinator.ClaimDueJobsAsync( _options.Claimant, _options.MaximumConcurrentJobs )
											.ConfigureAwait( false )
						);
				}
				catch( Exception exception )
				{
					_logger.LogError( exception, "Worker {Claimant} failed to claim due jobs.", _options.Claimant );
				}

				lock( _stateLock )
					_lastPoll = DateTime.UtcNow;

				if( jobs.Count > 0 )
				{
					await Task.WhenAll( jobs.Select( job => ProcessJobAsync( job, stoppingToken ) ) )
							.ConfigureAwait( false );

					// More work may already be due; poll again immediately.
					continue;
				}

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
					outcome = await _dispatcher.DispatchAsync( job.Context, execution.Token )
											.ConfigureAwait( false );

					outcome ??= new JobOutcome
					{
						Successful = false,
						Error = "Job dispatcher returned no outcome."
					};

					result = outcome.Successful ? RunResults.Succeeded : RunResults.Failed;
				}
				catch( OperationCanceledException ) when( timeout.IsCancellationRequested )
				{
					result = RunResults.TimedOut;

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
							"Worker {Claimant} abandoned run {Run} of schedule {Schedule}: {Reason}.",
							_options.Claimant, job.Context.Run, job.Context.Schedule,
							claimLost ? "claim lost" : "host stopping" );

					return;
				}
				catch( Exception exception )
				{
					// Third-party handler code: any escaping exception is an unsuccessful outcome.
					result = RunResults.Failed;

					outcome = new JobOutcome { Successful = false, Error = exception.ToString() };
				}
				finally
				{
					await heartbeatStop.CancelAsync().ConfigureAwait( false );

					await heartbeat.ConfigureAwait( false );
				}

				if( claimLost )
				{
					_logger.LogWarning(
							"Worker {Claimant} completed run {Run} of schedule {Schedule} after losing its claim; " +
							"outcome not reported.",
							_options.Claimant, job.Context.Run, job.Context.Schedule );

					return;
				}

				try
				{
					await _coordinator.ReportCompletionAsync( job.Context.Run, _options.Claimant, result, outcome )
									.ConfigureAwait( false );
				}
				catch( Exception exception )
				{
					_logger.LogError( exception,
									"Worker {Claimant} failed to report completion of run {Run} of schedule {Schedule}.",
									_options.Claimant, job.Context.Run, job.Context.Schedule );
				}
			}
			finally
			{
				Interlocked.Decrement( ref _activeJobs );
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
						renewed = await _coordinator.RenewClaimAsync( job.Context.Run, _options.Claimant )
													.ConfigureAwait( false );
					}
					catch( Exception exception )
					{
						_logger.LogWarning( exception,
											"Worker {Claimant} failed to renew claim of run {Run}; will retry.",
											_options.Claimant, job.Context.Run );

						continue;
					}

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
