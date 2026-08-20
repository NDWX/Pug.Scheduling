using System;

namespace Pug.Scheduling.Abstractions
{
	public record ExecutionCoordinatorOptions
	{
		// Validity of each acquired execution lease. Deliberately short: the coordinator renews
		// leases on worker heartbeats rather than sizing them to job timeouts, so crashed
		// workers' claims lapse quickly. Workers must heartbeat comfortably within this duration.
		public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes( 2 );
	}
}
