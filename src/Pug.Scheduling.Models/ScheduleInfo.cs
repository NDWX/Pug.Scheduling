using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Pug.Effable;

namespace Pug.Scheduling.Models
{
	// Composed top-level schedule entity: user-authored Definition, scheduler-maintained State,
	// the worker's ExecutionLease while claimed, and registration/update audit contexts.
	[DataContract]
	public record ScheduleInfo
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

		[Required, DataMember( IsRequired = true )]
		public ScheduleDefinition Definition
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Principal on whose behalf the job executes — assigned at registration (defaulting to
		// the registering principal), immutable thereafter, and deliberately not part of the
		// user-authored Definition. Jobs run under this principal's authority, not a system
		// identity, and this principal may always manage the schedule regardless of role-based
		// ACLs.
		[Required, DataMember( IsRequired = true )]
		public Reference Owner
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public ScheduleState State
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Nullable: present only while a worker instance holds the schedule's execution claim.
		// Maintained by the scheduler's IExecutionLeaseProvider in its own storage — composed
		// into this model for observability, never persisted with the schedule.
		[DataMember]
		public ExecutionLease ExecutionLease
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public ActionContext<Reference> Registration
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Nullable: a schedule has no update context until its definition is first modified.
		[DataMember]
		public ActionContext<Reference> LastUpdate
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
