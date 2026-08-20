using System.Runtime.Serialization;

namespace Pug.Scheduling.Worker
{
	// Runtime status snapshot of a worker instance, for management/diagnostics surfaces.
	[DataContract]
	public record WorkerInfo
	{
		[DataMember( IsRequired = true )]
		public string Claimant { get; init; }

		[DataMember( IsRequired = true )]
		public bool Paused { get; init; }

		// Null until the worker's first poll.
		[DataMember]
		public DateTime? LastPoll { get; init; }

		[DataMember( IsRequired = true )]
		public int ActiveJobs { get; init; }
	}
}
