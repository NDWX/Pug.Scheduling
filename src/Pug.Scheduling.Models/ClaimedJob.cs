using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// A due occurrence claimed by a worker through IExecutionCoordinator: the execution context
	// for the job handler plus the parameters the worker itself needs — the timeout to enforce
	// and the lease expiry to heartbeat against. Serializable so externally hosted workers can
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

		// UTC time at which the claim lapses unless renewed; the worker should heartbeat
		// comfortably before this.
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
	}
}
