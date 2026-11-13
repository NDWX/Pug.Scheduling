using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// User-authored portion of a schedule: what job it runs, and when. The owning principal is
	// assigned at registration and lives on ScheduleInfo, not here; runtime execution state
	// lives in ScheduleState/ExecutionLease, never here.
	[DataContract]
	public record ScheduleDefinition
	{
		// Tenant isolation scope, as in Pug.Authorized. Every query and authorization check is
		// qualified by this domain.
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

		[Required, DataMember( IsRequired = true )]
		public string Name
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Key of the job handler factory registered to execute this schedule's job.
		[Required, DataMember( IsRequired = true )]
		public string JobType
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Arbitrary job metadata as a JSON document, opaque to the scheduler. Interpreted only by
		// the job handler (and validated by the job type's IJobPayloadValidator, when registered).
		[DataMember]
		public string Payload
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public Recurrence Recurrence
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Nullable: registrations that omit it are assigned the service-level default.
		[DataMember]
		public MisfirePolicy MisfirePolicy
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Nullable: registrations that omit it are assigned the service-level default.
		[DataMember]
		public RetryPolicy RetryPolicy
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Maximum duration of a single job execution; the run's cancellation token is signalled
		// once elapsed. Always positive once registered: registrations that omit it
		// (TimeSpan.Zero) are assigned the service-level default.
		[DataMember]
		public TimeSpan Timeout
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
