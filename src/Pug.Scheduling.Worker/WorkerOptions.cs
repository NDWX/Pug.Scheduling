using Pug.Scheduling.Abstractions;

namespace Pug.Scheduling.Worker
{
	// Configuration of one worker group: Workers identical workers that claim under one claimant
	// identity, each holding its claims as "Claimant/{its host-wide worker number}".
	public record WorkerOptions
	{
		// Identity of the worker group: the claimant its grants are qualified by. Must not be blank
		// or contain '/'. Defaults to the machine name, which is stable across restarts: a
		// restarted worker presenting the same claimant and number re-takes its own lease and
		// adopts its unfinished run. Two worker processes on one machine must therefore set
		// distinct claimants.
		public string Claimant { get; init; } = Environment.MachineName;

		// Number of workers in the group, each claiming and executing one attended job at once.
		public int Workers { get; init; } = 1;

		// The only job type the group's workers claim; null claims any type.
		public string JobType { get; init; }

		// How long to wait between polls when no work was claimed, after a run whose report was not
		// recorded, and between retries of a completion report that did not get through (D29).
		// Positive, and within TimerBounds.Maximum.
		public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds( 10 );

		// Claim-renewal cadence during job execution. The worker caps it at a third of its window for
		// the claim (ClaimedJob.ClaimDuration, the coordinator's lease, less
		// SchedulerWorker.LeaseTolerance), so a longer value still leaves a renewal and a retry within
		// each lease; shorter leaves more retries. Positive, and within TimerBounds.Maximum.
		public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds( 30 );

		// How long a stopped run's handler (timed out, claim lost, host stopping) is given to honour
		// cancellation before the worker detaches it and moves on. Zero detaches immediately. Within
		// TimerBounds.Maximum. The default (10 s) is below the host's default shutdown timeout
		// (30 s); keep it below that, or a handler ignoring cancellation on shutdown outlasts the
		// host's wait for the worker.
		public TimeSpan CancellationGrace { get; init; } = TimeSpan.FromSeconds( 10 );

		// How long to wait before polling again after the coordinator refused a claim (NotAuthorized),
		// instead of PollInterval. A refusal means a missing CLAIM_JOBS grant or a security provider
		// failing closed, neither of which recovers within a poll interval, so a slower retry spares
		// the coordinator and its security backend while the worker still recovers on its own. Pause,
		// resume or the host stopping cut the wait short. Positive, and within TimerBounds.Maximum.
		public TimeSpan RefusedPollRetryInterval { get; init; } = TimeSpan.FromMinutes( 1 );

		// How many detached handlers (see CancellationGrace) each worker tolerates: at this many it
		// stops claiming until one ends. At least 1.
		public int MaxDetachedHandlers { get; init; } = 2;

		// How long the worker waits for the coordinator to release a claim it abandons on a
		// graceful shutdown. Best effort: past this the call is given up on (the lease then lapses
		// on its own), so it bounds the delay this adds to the host's shutdown. Positive, and within
		// TimerBounds.Maximum.
		public TimeSpan AbandonTimeout { get; init; } = TimeSpan.FromSeconds( 5 );

		/// <summary>
		/// Throws <see cref="ArgumentException"/> (naming the offending property) unless
		/// <see cref="Claimant"/> is neither blank nor contains '/', <see cref="Workers"/> is at
		/// least 1, <see cref="JobType"/> is null or not blank, <see cref="PollInterval"/>,
		/// <see cref="HeartbeatInterval"/> and <see cref="RefusedPollRetryInterval"/> are positive,
		/// <see cref="CancellationGrace"/> is not negative, <see cref="AbandonTimeout"/> is positive,
		/// each of <see cref="AbandonTimeout"/>, <see cref="PollInterval"/>,
		/// <see cref="RefusedPollRetryInterval"/>, <see cref="HeartbeatInterval"/> and
		/// <see cref="CancellationGrace"/> is within <see cref="TimerBounds.Maximum"/>, and
		/// <see cref="MaxDetachedHandlers"/> is at least 1.
		/// </summary>
		public void Validate()
		{
			if( string.IsNullOrWhiteSpace( Claimant ) )
				throw new ArgumentException( "Worker claimant is required.", nameof(Claimant) );

			if( Claimant.Contains( '/' ) )
				throw new ArgumentException( "Worker claimant must not contain '/'.", nameof(Claimant) );

			if( Workers < 1 )
				throw new ArgumentException( "A worker group must have at least one worker.", nameof(Workers) );

			if( JobType is not null && string.IsNullOrWhiteSpace( JobType ) )
				throw new ArgumentException( "Worker job type must be null (any type) or not blank.", nameof(JobType) );

			if( PollInterval <= TimeSpan.Zero )
				throw new ArgumentException( "Poll interval must be positive.", nameof(PollInterval) );

			if( HeartbeatInterval <= TimeSpan.Zero )
				throw new ArgumentException( "Heartbeat interval must be positive.", nameof(HeartbeatInterval) );

			if( CancellationGrace < TimeSpan.Zero )
				throw new ArgumentException( "Cancellation grace must not be negative.", nameof(CancellationGrace) );

			if( AbandonTimeout <= TimeSpan.Zero )
				throw new ArgumentException( "Abandon timeout must be positive.", nameof(AbandonTimeout) );

			if( RefusedPollRetryInterval <= TimeSpan.Zero )
				throw new ArgumentException( "Refused poll retry interval must be positive.", nameof(RefusedPollRetryInterval) );

			ValidateWithinTimerBounds( AbandonTimeout, nameof(AbandonTimeout) );
			ValidateWithinTimerBounds( PollInterval, nameof(PollInterval) );
			ValidateWithinTimerBounds( RefusedPollRetryInterval, nameof(RefusedPollRetryInterval) );
			ValidateWithinTimerBounds( HeartbeatInterval, nameof(HeartbeatInterval) );
			ValidateWithinTimerBounds( CancellationGrace, nameof(CancellationGrace) );

			if( MaxDetachedHandlers < 1 )
				throw new ArgumentException( "Maximum detached handlers must be at least 1.", nameof(MaxDetachedHandlers) );
		}

		private static void ValidateWithinTimerBounds( TimeSpan duration, string property )
		{
			if( duration > TimerBounds.Maximum )
				throw new ArgumentException( $"Must not exceed {TimerBounds.Maximum}.", property );
		}
	}
}
