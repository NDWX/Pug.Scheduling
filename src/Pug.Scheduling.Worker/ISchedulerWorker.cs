namespace Pug.Scheduling.Worker
{
	// Management surface of the worker: pause/resume claiming and inspect runtime status.
	// Pausing stops new claims; jobs already executing run to completion. Authorization is the
	// hosting surface's concern (e.g. admin-gated management endpoints).
	public interface ISchedulerWorker
	{
		WorkerInfo GetInfo();

		void Pause();

		void Resume();
	}
}
