using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Execution outcome of one occurrence, reported by the job handler. Handler implementations
	// are third-party code, so the worker never assumes a well-behaved return: any exception
	// escaping the handler is caught and materialized as an unsuccessful outcome carrying the
	// exception in Error, and a null return is likewise treated as unsuccessful. Unsuccessful
	// outcomes are retried per the schedule's RetryPolicy.
	[DataContract]
	public record JobOutcome
	{
		[Required, DataMember( IsRequired = true )]
		public bool Successful
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Optional handler-supplied outcome as a JSON document (e.g. output artifact reference,
		// delivery receipt), recorded verbatim in run history. Opaque to the scheduler.
		[DataMember]
		public string Details
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Description of the exception caught by (or escaping) the job handler; null when
		// Successful.
		[DataMember]
		public string Error
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
