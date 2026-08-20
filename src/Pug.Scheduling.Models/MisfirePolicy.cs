using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// How occurrences missed while no worker was running are handled once a worker returns.
	// An occurrence counts as misfired when it is overdue by more than Threshold.
	[DataContract]
	public record MisfirePolicy
	{
		// Well-known MisfireHandlings value (FIRE_ONCE / SKIP / RUN_ALL).
		[Required, DataMember( IsRequired = true )]
		public string Handling
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Overdue tolerance before an occurrence is considered misfired rather than merely late.
		[Required, DataMember( IsRequired = true )]
		public TimeSpan Threshold
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
