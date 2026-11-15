using System;

namespace Pug.Scheduling.Abstractions
{
	public record ExecutionCoordinatorOptions
	{
		// Validity of each acquired execution lease. Deliberately short: the coordinator renews
		// leases on worker heartbeats rather than sizing them to job timeouts, so crashed
		// workers' claims lapse quickly. Workers must heartbeat comfortably within this duration.
		// Expiry is stamped and compared with each coordinator host's own clock, so the duration
		// must also exceed any clock skew between hosts.
		public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes( 2 );

		// Number of due candidates read per claim. A claim returns the first of them it can take,
		// so a worker that loses the race for the oldest due schedule to a peer, or meets a
		// schedule it fails to claim, moves on to the next instead of coming back empty while other
		// work is due.
		public int ClaimCandidates { get; init; } = 10;
	}
}
