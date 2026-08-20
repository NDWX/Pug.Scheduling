using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// How failed executions of a single occurrence are retried before the occurrence is recorded
	// as a terminal failure and the schedule advances to its next occurrence.
	[DataContract]
	public record RetryPolicy
	{
		// Total attempts per occurrence, including the first. 1 disables retries.
		[Required, DataMember( IsRequired = true )]
		public int MaximumAttempts
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Base delay before the first retry; subsequent retries back off exponentially
		// (Backoff * 2^attempt).
		[Required, DataMember( IsRequired = true )]
		public TimeSpan Backoff
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
