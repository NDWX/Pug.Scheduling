using System;

namespace Pug.Scheduling.Abstractions
{
	public record ExecutionCoordinatorOptions
	{
		// Validity of each acquired execution lease. Deliberately short: the coordinator renews
		// leases on worker heartbeats rather than sizing them to job timeouts, so crashed
		// workers' claims lapse quickly. Workers must heartbeat comfortably within this duration.
		// Expiry is stamped and compared on the database's clock by default, so coordinator hosts'
		// clock skew does not bear on it; a host that opts into a host clock (TimeProviderClock)
		// makes the duration sensitive to that skew again. A forward step of the database server's
		// clock shortens every live lease by the size of the step, so the duration must still
		// comfortably exceed the workers' HeartbeatInterval plus any such step.
		public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes( 2 );

		// Number of due candidates read per claim. A claim returns the first of them it can take,
		// so a worker that loses the race for the oldest due schedule to a peer, or meets a
		// schedule it fails to claim, moves on to the next instead of coming back empty while other
		// work is due.
		public int ClaimCandidates { get; init; } = 10;
	}
}
