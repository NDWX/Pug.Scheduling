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
	private volatile string? _fault;

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
			Fault = _fault!,
			LastPoll = null,
			ActiveJobs = 0
		};

	public void Pause() => _paused = true;

	// As SchedulerWorker does when the coordinator refuses it: paused, with the reason.
	internal void Fault( string fault )
	{
		_fault = fault;
		_paused = true;
	}

	// Clears a fault, as SchedulerWorker does.
	public void Resume()
	{
		_paused = false;
		_fault = null;
	}
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
