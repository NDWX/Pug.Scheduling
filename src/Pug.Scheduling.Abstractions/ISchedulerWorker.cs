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

		// Stops new claims. Sets no fault.
		void Pause();

		// Lifts a pause, whether an operator's or the worker's own: a worker pauses itself when the
		// coordinator refuses a renewal or completion report, and says why in WorkerInfo.Fault.
		// Resuming clears that fault, so grant the missing operation first, or the next run meets
		// the same refusal.
		void Resume();
	}
}
