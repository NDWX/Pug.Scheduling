using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Stands in for the hosted <c>SchedulerWorker</c> in endpoint tests: the management endpoints
/// only need the surface, and a real worker would claim the schedules the tests register.
/// </summary>
internal sealed class FakeSchedulerWorker : ISchedulerWorker
{
	internal const string Claimant = "test-worker";

	private bool _paused;

	public WorkerInfo GetInfo() =>
		new()
		{
			Claimant = Claimant,
			Paused = _paused,
			LastPoll = null,
			ActiveJobs = 0
		};

	public void Pause() => _paused = true;

	public void Resume() => _paused = false;
}
