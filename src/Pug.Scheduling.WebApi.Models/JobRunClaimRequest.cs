using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.WebApi.Models
{
	// Request body of IExecutionCoordinator.ClaimDueJobAsync when the coordinator is reached over
	// the wire by an externally hosted worker: an HTTP surface contract, not a domain model —
	// nothing but the transport uses it.
	[DataContract]
	public record JobRunClaimRequest
	{
		// Identity of the worker group the claim is made as; grants are qualified by it. Must not
		// contain '/'.
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

		// Number of the claiming worker within its group (1 or greater); the claim's lease is
		// held as "Claimant/Worker".
		[Required, DataMember( IsRequired = true )]
		public int Worker
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Restricts the claim to this job type; null claims any type.
		[DataMember]
		public string JobType
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
