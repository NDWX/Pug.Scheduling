using System;

namespace Pug.Scheduling.Abstractions
{
	public record ExecutionCoordinatorOptions
	{
		// The shortest LeaseDuration the coordinator accepts. Workers renew a third of the way into
		// their window (the lease less 0.5 s) at most, so a lease of seconds would leave a renewal's
		// round trip, a slow store or a clock step no margin, and claims would be lost and runs
		// executed again.
		public static readonly TimeSpan MinimumLeaseDuration = TimeSpan.FromSeconds( 10 );

		// Validity of each acquired execution lease. Deliberately short: the coordinator renews
		// leases on worker heartbeats rather than sizing them to job timeouts, so crashed
		// workers' claims lapse quickly. Workers must heartbeat comfortably within this duration.
		// Expiry is stamped and compared on the database's clock by default, so coordinator hosts'
		// clock skew does not bear on it; a host that opts into a host clock (TimeProviderClock)
		// makes the duration sensitive to that skew again. A forward step of the database server's
		// clock shortens every live lease by the size of the step, so the duration must still
		// comfortably exceed a renewal's round trip plus any such step. At least MinimumLeaseDuration
		// and within TimerBounds.Maximum (the coordinator rejects anything else). Handed to every
		// claim as ClaimedJob.ClaimDuration, which the worker times its claim by; a renewal restarts
		// the full ClaimDuration, so every coordinator instance on one store must share the same
		// LeaseDuration.
		public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes( 2 );

		// Number of due candidates read per claim. A claim returns the first of them it can take,
		// so a worker that loses the race for the oldest due schedule to a peer, or meets a
		// schedule it fails to claim, moves on to the next instead of coming back empty while other
		// work is due.
		public int ClaimCandidates { get; init; } = 10;
	}
}
