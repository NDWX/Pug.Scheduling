using System;
using System.ComponentModel.DataAnnotations;
using System.Runtime.Serialization;

namespace Pug.Scheduling.Models
{
	// Runtime status snapshot of a worker instance, for management/diagnostics surfaces. Lives
	// with the models (rather than the worker implementation) so management surfaces — the
	// WebApi and its client — can report worker status without depending on the worker host.
	[DataContract]
	public record WorkerInfo
	{
		// Identity the worker claims jobs under; also the identifier its management ACLs are
		// qualified by.
		[Required, DataMember( IsRequired = true )]
		public string Claimant
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

		// Null until the worker's first poll.
		[DataMember]
		public DateTime? LastPoll
		{
			get;
#if NET5_0_OR_GREATER
			init;
#else
			set;
#endif
		}

		[Required, DataMember( IsRequired = true )]
		public int ActiveJobs
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
