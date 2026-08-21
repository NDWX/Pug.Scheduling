using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Request body of IExecutionCoordinator.ClaimDueJobsAsync when the coordinator is reached over
	// the wire by an externally hosted worker. Lives with the models rather than the WebApi so a
	// worker host's coordinator client need not reference the server-side package.
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
