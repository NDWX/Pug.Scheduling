using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	public static class IServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the <see cref="SchedulerWorker"/> hosted service (and its management surface
		/// <see cref="ISchedulerWorker"/>) and the <see cref="IScheduledJobHandlerFactoryRegistry"/>
		/// that resolves handler factories registered with
		/// <see cref="AddScheduledJobHandler{THandler}"/> or
		/// <see cref="AddScheduledJobHandlerFactory{TFactory}"/>. The host must provide an
		/// <see cref="IExecutionCoordinator"/> (in-process, or the HTTP client) and logging.
		/// </summary>
		public static IServiceCollection AddSchedulingWorker( this IServiceCollection serviceCollection,
															WorkerOptions options = null )
		{
			serviceCollection.TryAddSingleton<IScheduledJobHandlerFactoryRegistry>(
					provider => new JobHandlerFactoryRegistry( provider ) );

			serviceCollection.AddSingleton(
					provider => new SchedulerWorker(
							provider.GetRequiredService<IExecutionCoordinator>(),
							provider.GetRequiredService<IScheduledJobHandlerFactoryRegistry>(),
							options ?? new WorkerOptions(),
							provider.GetRequiredService<ILogger<SchedulerWorker>>()
						)
				);

			serviceCollection.AddSingleton<ISchedulerWorker>(
					provider => provider.GetRequiredService<SchedulerWorker>() );

			serviceCollection.AddHostedService( provider => provider.GetRequiredService<SchedulerWorker>() );

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
	}
}
