using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Claim held by a worker on the schedule's active run. Stored on the schedule row and written
	// only by the execution coordinator's transitions: set when a run starts or is adopted,
	// heartbeat-renewed during long runs, and cleared when the run completes. Expiry is what makes
	// crashed workers recoverable: once it passes, a peer may adopt the unfinished run, yielding
	// at-least-once execution. A lapsed lease stays on the schedule until its run is adopted or
	// completed, so a schedule with a lease always has a run in progress.
	[DataContract]
	public record ExecutionLease
	{
		// The schedule's active run: the run the claim is on.
		[Required, DataMember( IsRequired = true )]
		public string Run
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// The worker holding the claim, as "{claimant}/{worker}".
		[Required, DataMember( IsRequired = true )]
		public string ClaimedBy
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// UTC time after which the claim no longer excludes other workers.
		[Required, DataMember( IsRequired = true )]
		public DateTime Expiry
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}
	}
}
