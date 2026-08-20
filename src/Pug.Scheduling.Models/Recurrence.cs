using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// When the schedule fires. Exactly one of CronExpression or Interval must be specified.
	[DataContract]
	public record Recurrence
	{
		// Cron expression (Cronos syntax). Occurrence times are computed in TimeZone and stored
		// as UTC. Null when Interval recurrence is used instead.
		[DataMember]
		public string CronExpression
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Simple fixed-interval recurrence, measured from the previous occurrence. Null when
		// CronExpression is used instead.
		[DataMember]
		public TimeSpan? Interval
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// IANA time zone identifier (e.g. 'Australia/Melbourne') in which CronExpression is
		// evaluated, so occurrences follow the owner's local time across DST transitions.
		[Required, DataMember( IsRequired = true )]
		public string TimeZone
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		// Optional activation window (UTC, inclusive): no occurrences before NotBefore or after
		// NotAfter. A schedule past NotAfter has no next occurrence and is effectively exhausted.
		[DataMember]
		public DateTime? NotBefore
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[DataMember]
		public DateTime? NotAfter
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
