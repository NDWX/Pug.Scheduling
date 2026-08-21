using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.WebApi.Models
{
	// Request body of IExecutionCoordinator.ClaimDueJobsAsync when the coordinator is reached over
	// the wire by an externally hosted worker: an HTTP surface contract, not a domain model —
	// nothing but the transport uses it.
	[DataContract]
	public record JobRunClaimRequest
	{
		// Identity the claiming worker instance holds its leases under.
		[Required, DataMember( IsRequired = true )]
		public string Claimant
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Upper bound on the number of due occurrences to claim in this poll.
		[Required, DataMember( IsRequired = true )]
		public int MaximumJobs
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
