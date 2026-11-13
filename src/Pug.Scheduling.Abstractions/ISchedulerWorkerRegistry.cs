using System.Collections.Generic;

namespace Pug.Scheduling.Abstractions
{
	// The workers hosted in this process, addressed by their host-wide worker number. Management
	// surfaces reach individual ISchedulerWorker instances through it; like ISchedulerWorker it
	// performs no authorization.
	public interface ISchedulerWorkerRegistry
	{
		// Every hosted worker, in worker-number order.
		IReadOnlyCollection<ISchedulerWorker> Workers { get; }

		// The worker with the given number, or null when this host has none.
		ISchedulerWorker Find( int worker );
	}
}
