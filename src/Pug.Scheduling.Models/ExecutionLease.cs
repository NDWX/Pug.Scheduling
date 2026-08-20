using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Claim held by a worker instance while it executes the schedule's due occurrence. A schedule
	// with no lease (or an expired one) is claimable; the lease is heartbeat-renewed during long
	// runs and cleared on completion. Expiry is what makes crashed workers recoverable: peers may
	// reclaim once it passes, yielding at-least-once execution. Leases are arbitrated and stored
	// by IExecutionLeaseProvider, independently of the schedule store.
	[DataContract]
	public record ExecutionLease
	{
		// Unique identifier of the worker instance holding the claim.
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
