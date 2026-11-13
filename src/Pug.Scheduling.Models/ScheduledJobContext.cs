using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;
using Pug.Effable;

namespace Pug.Scheduling.Models
{
	// Everything a job handler needs to execute one occurrence of a schedule. Serializable so
	// handler factories that run the job elsewhere (e.g. one calling a webhook) can carry it across the wire
	// without contract changes.
	[DataContract]
	public record ScheduledJobContext
	{
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

		// Identifier of this execution's ScheduleRun record, for correlating handler output and
		// logs with run history.
		[Required, DataMember( IsRequired = true )]
		public string Run
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

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

		// Principal on whose behalf the job executes. Handlers with security-sensitive work must
		// establish this principal as the ambient security context before acting, so data
		// authorization is evaluated as the owner rather than a system identity.
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
		public string JobType
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// The schedule definition's job metadata JSON, opaque to the scheduler.
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

		// The occurrence being executed (UTC planned fire time). With Schedule this is the
		// idempotency key handlers should use for externally visible side effects, so a retried
		// occurrence is not double-applied.
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

		// Zero-based retry attempt of this occurrence.
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
	}
}
