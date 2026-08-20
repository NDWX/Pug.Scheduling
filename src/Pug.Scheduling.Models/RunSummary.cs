using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Condensed outcome of a schedule's most recent execution, carried in ScheduleState for
	// list views; full history lives in ScheduleRun records.
	[DataContract]
	public record RunSummary
	{
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

		// Well-known RunResults value; null while the run is still executing.
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
	}
}
