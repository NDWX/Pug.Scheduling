using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Runtime scheduling state, maintained exclusively by the scheduler and worker — never
	// authored by users. Lives alongside the definition on the same schedule row but is modelled
	// separately so definition edits and execution progress cannot be conflated.
	[DataContract]
	public record ScheduleState
	{
		[Required, DataMember( IsRequired = true )]
		public bool Enabled
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public bool Paused
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Next occurrence in UTC. Null when the schedule is disabled or its recurrence is
		// exhausted (past Recurrence.NotAfter).
		[DataMember]
		public DateTime? NextRun
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// True when an immediate run has been explicitly requested (run-now). Consumed by the
		// execution coordinator at claim time to classify the resulting run as MANUAL.
		[Required, DataMember( IsRequired = true )]
		public bool RunRequested
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Zero-based retry attempt of the current due occurrence; reset to 0 once the occurrence
		// completes (successfully or terminally).
		[Required, DataMember( IsRequired = true )]
		public int Attempt
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Nullable: a schedule has no run summary until its first execution.
		[DataMember]
		public RunSummary LastRun
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
