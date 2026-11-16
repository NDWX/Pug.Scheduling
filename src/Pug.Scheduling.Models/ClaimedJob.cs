using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// A due occurrence claimed by a worker through IExecutionCoordinator: the execution context
	// for the job handler plus the parameters the worker itself needs — the timeout to enforce
	// and how long its claim stands unrenewed. Serializable so externally hosted workers can
	// receive claims over the wire unchanged.
	[DataContract]
	public record ClaimedJob
	{
		[Required, DataMember( IsRequired = true )]
		public ScheduledJobContext Context
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Maximum execution duration the worker must enforce on this run.
		[Required, DataMember( IsRequired = true )]
		public TimeSpan Timeout
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// UTC time at which the claim lapses unless renewed. It is on the coordinator's clock (the
		// database's by default), not the worker's, so it is informational: a worker judges its
		// claim by ClaimDuration on its own clock instead.
		[Required, DataMember( IsRequired = true )]
		public DateTime ClaimExpiry
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// How long the claim stands without a renewal: the coordinator's lease duration, which a
		// successful renewal restarts. A clock-free duration, so the worker can time its claim on
		// its own clock from the moment it sent the request — a deadline that skew and round trip
		// can only move earlier than the coordinator's, never later (D29).
		[Required, DataMember( IsRequired = true )]
		public TimeSpan ClaimDuration
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
