using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	// One AddSchedulingWorkers call: its options, its optional coordinator factory, and the
	// host-wide number of its first worker.
	internal sealed record SchedulerWorkerGroup(
		WorkerOptions Options,
		Func<IServiceProvider, IExecutionCoordinator> Coordinator,
		int FirstWorker );

	// Collects worker groups declared during service registration. Kept as a mutable singleton
	// instance in the service collection so successive AddSchedulingWorkers calls accumulate into
	// one host-wide numbering (1..N in registration order).
	internal sealed class SchedulerWorkerRegistrations
	{
		public List<SchedulerWorkerGroup> Groups { get; } = new();

		// Workers registered so far, which is also the highest worker number assigned.
		public int Workers { get; private set; }

		public SchedulerWorkerGroup Add( WorkerOptions options, Func<IServiceProvider, IExecutionCoordinator> coordinator )
		{
			SchedulerWorkerGroup group = new( options, coordinator, Workers + 1 );

			Groups.Add( group );
			Workers += options.Workers;

			return group;
		}

		// Creates every registered worker. Each group's coordinator is resolved once and shared by
		// the group's workers.
		public SchedulerWorkerRegistry CreateRegistry( IServiceProvider provider )
		{
			IScheduledJobHandlerFactoryRegistry handlerFactories =
				provider.GetRequiredService<IScheduledJobHandlerFactoryRegistry>();

			ILogger<SchedulerWorker> logger = provider.GetRequiredService<ILogger<SchedulerWorker>>();

			List<SchedulerWorker> workers = new( Workers );

			foreach( SchedulerWorkerGroup group in Groups )
			{
				IExecutionCoordinator coordinator = group.Coordinator is null
														? provider.GetRequiredService<IExecutionCoordinator>()
														: group.Coordinator( provider ) ??
														throw new InvalidOperationException(
																$"The coordinator factory of worker group '{group.Options.Claimant}' " +
																"returned no coordinator." );

				for( int index = 0; index < group.Options.Workers; index++ )
					workers.Add(
							new SchedulerWorker(
									coordinator, handlerFactories, group.Options, group.FirstWorker + index, logger ) );
			}

			return new SchedulerWorkerRegistry( workers );
		}
	}
}
