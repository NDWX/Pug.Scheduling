using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Append-only record of one execution attempt of a schedule: the execution audit trail.
	// (Schedule, ScheduledFor, Attempt) is unique — the at-least-once deduplication backstop.
	[DataContract]
	public record ScheduleRun
	{
		[Required, DataMember( IsRequired = true )]
		public string Identifier
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Identifier of the schedule this run belongs to.
		[Required, DataMember( IsRequired = true )]
		public string Schedule
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Denormalized from the schedule so run history remains domain-filterable on its own.
		[Required, DataMember( IsRequired = true )]
		public string Domain
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// The occurrence this run executes (UTC): the planned fire time, not when execution
		// actually started. Together with Schedule and Attempt this is the run's idempotency key.
		[Required, DataMember( IsRequired = true )]
		public DateTime ScheduledFor
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public DateTime Started
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Null while the run is still executing.
		[DataMember]
		public DateTime? Finished
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Zero-based retry attempt of the occurrence this run executes.
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

		// Well-known RunTriggers value: what initiated this run.
		[Required, DataMember( IsRequired = true )]
		public string Trigger
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Well-known RunResults value — the worker's classification of the run (a timeout or a
		// quota/misfire skip is not the handler's report); null while the run is still executing.
		[DataMember]
		public string Result
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Outcome reported by the job handler; null while the run is still executing, and for
		// runs that never reached the handler (e.g. SKIPPED).
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

		// The lease holder ("{claimant}/{worker}") the run is recorded as executing under; a
		// completion is accepted only from it. Null for runs recorded before this was tracked.
		[DataMember]
		public string ClaimedBy
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
