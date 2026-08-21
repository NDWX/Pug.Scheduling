using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.WebApi.Models
{
	// Request body of IExecutionCoordinator.ReportCompletionAsync when the coordinator is reached
	// over the wire by an externally hosted worker.
	[DataContract]
	public record JobRunCompletion
	{
		// The worker's classification of the run — a well-known RunResults value.
		[Required, DataMember( IsRequired = true )]
		public string Result
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// The job handler's own report, null for runs that never reached the handler (e.g.
		// SKIPPED, TIMED_OUT).
		[DataMember]
		public JobOutcome Outcome
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
