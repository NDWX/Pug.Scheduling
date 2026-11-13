using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	public static class IServiceCollectionExtensions
	{
		/// <summary>
		/// Registers a group of <see cref="WorkerOptions.Workers"/> identical
		/// <see cref="SchedulerWorker"/> hosted services claiming as <see cref="WorkerOptions.Claimant"/>
		/// (and only <see cref="WorkerOptions.JobType"/>, if set). Call once per group. Worker numbers
		/// are host-wide and deterministic: 1..N across all groups, in registration order, so a
		/// restarted host re-takes its own claims.
		/// Also registers <see cref="ISchedulerWorkerRegistry"/> (the management surface over every
		/// hosted worker) and the <see cref="IScheduledJobHandlerFactoryRegistry"/> that resolves
		/// handler factories registered with <see cref="AddScheduledJobHandler{THandler}"/> or
		/// <see cref="AddScheduledJobHandlerFactory{TFactory}"/>. The host must provide logging.
		/// </summary>
		/// <param name="serviceCollection">Services.</param>
		/// <param name="options">The group's configuration; defaults to <c>new WorkerOptions()</c>.</param>
		/// <param name="coordinator">
		/// Supplies the group's <see cref="IExecutionCoordinator"/> — e.g. an
		/// <c>ExecutionCoordinatorClient</c> carrying the group's own credentials. Invoked once per
		/// group; the group's workers share the result. When omitted, the group resolves
		/// <see cref="IExecutionCoordinator"/> (in-process, or the HTTP client) from the container.
		/// </param>
		/// <exception cref="ArgumentException">The options are invalid (see <see cref="WorkerOptions.Validate"/>).</exception>
		public static IServiceCollection AddSchedulingWorkers( this IServiceCollection serviceCollection,
																WorkerOptions options = null,
																Func<IServiceProvider, IExecutionCoordinator> coordinator =
																	null )
		{
			if( serviceCollection is null ) throw new ArgumentNullException( nameof(serviceCollection) );

			options ??= new WorkerOptions();
			options.Validate();

			SchedulerWorkerRegistrations registrations = GetOrAddRegistrations( serviceCollection );

			SchedulerWorkerGroup group = registrations.Add( options, coordinator );

			serviceCollection.TryAddSingleton<IScheduledJobHandlerFactoryRegistry>(
					provider => new JobHandlerFactoryRegistry( provider ) );

			// Built once from the final registrations, after every group has been added.
			serviceCollection.TryAddSingleton<SchedulerWorkerRegistry>( registrations.CreateRegistry );

			serviceCollection.TryAddSingleton<ISchedulerWorkerRegistry>(
					provider => provider.GetRequiredService<SchedulerWorkerRegistry>() );

			// One IHostedService registration per worker, each resolving the registry's instance.
			// Not AddHostedService: it uses TryAddEnumerable, which deduplicates factory
			// registrations by implementation type and would silently drop every worker after the
			// first.
			for( int index = 0; index < options.Workers; index++ )
			{
				int worker = group.FirstWorker + index;

				serviceCollection.AddSingleton<IHostedService>(
						provider => provider.GetRequiredService<SchedulerWorkerRegistry>().Find( worker ) );
			}

			return serviceCollection;
		}

		/// <summary>
		/// Registers a job handler for the job type (keyed, scoped — resolved from a dedicated
		/// scope per run, disposed with the run). Does not declare the job type schedulable; hosts
		/// that accept schedules for it call <c>AddSchedulableJobType</c> from
		/// Pug.Scheduling.DependencyInjection.
		/// </summary>
		public static IServiceCollection AddScheduledJobHandler<THandler>( this IServiceCollection serviceCollection,
																			string jobType )
			where THandler : class, IScheduledJobHandler
		{
			ValidateJobType( jobType );

			serviceCollection.AddKeyedScoped<IScheduledJobHandler, THandler>( jobType );

			serviceCollection.AddKeyedSingleton<IScheduledJobHandlerFactory>(
					jobType,
					( provider, _ ) => new ServiceProviderJobHandlerFactory(
							provider.GetRequiredService<IServiceScopeFactory>(), jobType ) );

			return serviceCollection;
		}

		/// <summary>
		/// Registers a custom handler factory for the job type (keyed singleton), e.g. one whose
		/// handlers run the job elsewhere by calling a webhook.
		/// </summary>
		public static IServiceCollection AddScheduledJobHandlerFactory<TFactory>(
			this IServiceCollection serviceCollection, string jobType )
			where TFactory : class, IScheduledJobHandlerFactory
		{
			ValidateJobType( jobType );

			serviceCollection.AddKeyedSingleton<IScheduledJobHandlerFactory, TFactory>( jobType );

			return serviceCollection;
		}

		private static void ValidateJobType( string jobType )
		{
			if( string.IsNullOrWhiteSpace( jobType ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(jobType) );
		}

		private static SchedulerWorkerRegistrations GetOrAddRegistrations( IServiceCollection serviceCollection )
		{
			ServiceDescriptor existing = serviceCollection.FirstOrDefault(
					descriptor => descriptor.ServiceType == typeof(SchedulerWorkerRegistrations) &&
								descriptor.ImplementationInstance is not null );

			if( existing?.ImplementationInstance is SchedulerWorkerRegistrations registrations )
				return registrations;

			SchedulerWorkerRegistrations created = new();

			serviceCollection.AddSingleton( created );

			return created;
		}
	}
}
