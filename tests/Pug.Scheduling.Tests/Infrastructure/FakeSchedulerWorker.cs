using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Stands in for a hosted <c>SchedulerWorker</c> in endpoint tests: the management endpoints
/// only need the surface, and a real worker would claim the schedules the tests register.
/// </summary>
internal sealed class FakeSchedulerWorker : ISchedulerWorker
{
	private readonly string _claimant;
	private readonly int _worker;
	private readonly string? _jobType;

	private volatile bool _paused;

	internal FakeSchedulerWorker( string claimant, int worker, string? jobType = null )
	{
		_claimant = claimant;
		_worker = worker;
		_jobType = jobType;
	}

	public WorkerInfo GetInfo() =>
		new()
		{
			Claimant = _claimant,
			Worker = _worker,
			JobType = _jobType!,
			Paused = _paused,
			LastPoll = null,
			ActiveJobs = 0
		};

	public void Pause() => _paused = true;

	public void Resume() => _paused = false;
}

/// <summary>
/// Registry over a fixed set of <see cref="ISchedulerWorker"/> instances, addressed by the
/// number each reports.
/// </summary>
internal sealed class FakeSchedulerWorkerRegistry : ISchedulerWorkerRegistry
{
	private readonly ISchedulerWorker[] _workers;

	internal FakeSchedulerWorkerRegistry( params ISchedulerWorker[] workers )
	{
		_workers = workers;
	}

	public IReadOnlyCollection<ISchedulerWorker> Workers => _workers;

	public ISchedulerWorker Find( int worker ) =>
		_workers.FirstOrDefault( candidate => candidate.GetInfo().Worker == worker )!;
}
