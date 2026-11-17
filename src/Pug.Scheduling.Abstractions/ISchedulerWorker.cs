using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Management surface of a worker instance: pause/resume claiming and inspect runtime status.
	// Pausing stops new claims; jobs already executing run to completion. Authorization is the
	// hosting surface's concern (see WorkerSecurityOperations, enforced by the management
	// endpoints) — the worker itself performs no authorization.
	//
	// Deliberately synchronous: this manages the locally hosted worker instance, not a remote
	// one. Workers still reach the scheduler only through IExecutionCoordinator.
	public interface ISchedulerWorker
	{
		WorkerInfo GetInfo();

		// Stops new claims. Only an operator pauses a worker; it never pauses itself.
		void Pause();

		// Lifts a pause. WorkerInfo.Fault is not the worker's to clear: it reports what the
		// coordinator is doing, and clears when the coordinator answers again.
		void Resume();
	}
}
