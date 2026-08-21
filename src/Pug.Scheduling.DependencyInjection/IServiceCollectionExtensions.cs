using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Worker;

namespace Pug.Scheduling.DependencyInjection
{
	public static class IServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the scheduling management facade (<see cref="IScheduler"/>) and execution
		/// coordinator (<see cref="IExecutionCoordinator"/>). The host is expected to have
		/// already registered <see cref="IApplicationData{IDataSession}"/> for the schedule store
		/// (a concrete provider such as the Postgres or SQLite <c>DapperData</c>),
		/// <see cref="IExecutionLeaseProvider"/> (e.g. <c>DatabaseExecutionLeaseProvider</c> over
		/// its own lease store), and <see cref="ISecurityManager"/>.
		/// </summary>
		public static IServiceCollection AddScheduling( this IServiceCollection serviceCollection,
														IdentifierGenerator identifierGenerator,
														SchedulerOptions options = null,
														ExecutionCoordinatorOptions coordinatorOptions = null )
		{
			if( identifierGenerator == null ) throw new ArgumentNullException( nameof(identifierGenerator) );

			JobTypeRegistrations registrations = GetOrAddRegistrations( serviceCollection );

			serviceCollection.TryAddSingleton<IJobTypeRegistry>(
					provider => new JobTypeRegistry( registrations, provider )
				);

			serviceCollection.TryAddSingleton<IScheduler>(
					provider => new Scheduler(
							identifierGenerator,
							provider.GetRequiredService<IApplicationData<IDataSession>>(),
							provider.GetRequiredService<ISecurityManager>(),
							provider.GetRequiredService<IExecutionLeaseProvider>(),
							provider.GetRequiredService<IJobTypeRegistry>(),
							options ?? new SchedulerOptions()
						)
				);

			serviceCollection.TryAddSingleton<IExecutionCoordinator>(
					provider => new ExecutionCoordinator(
							identifierGenerator,
							provider.GetRequiredService<IApplicationData<IDataSession>>(),
							provider.GetRequiredService<IExecutionLeaseProvider>(),
							provider.GetRequiredService<ISecurityManager>(),
							coordinatorOptions ?? new ExecutionCoordinatorOptions()
						)
				);

			return serviceCollection;
		}

		/// <summary>
		/// Registers the <see cref="SchedulerWorker"/> hosted service (and its management surface
		/// <see cref="ISchedulerWorker"/>). Registers <see cref="InProcessJobDispatcher"/> as the
		/// default <see cref="IScheduledJobDispatcher"/> unless the host registered another
		/// transport first.
		/// </summary>
		public static IServiceCollection AddSchedulingWorker( this IServiceCollection serviceCollection,
															WorkerOptions options = null )
		{
			serviceCollection.TryAddSingleton<IScheduledJobDispatcher, InProcessJobDispatcher>();

			serviceCollection.AddSingleton(
					provider => new SchedulerWorker(
							provider.GetRequiredService<IExecutionCoordinator>(),
							provider.GetRequiredService<IScheduledJobDispatcher>(),
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
		/// Registers a job handler for the job type (keyed, scoped — resolved per run by the
		/// in-process dispatcher) and declares the job type schedulable.
		/// </summary>
		public static IServiceCollection AddScheduledJobHandler<THandler>( this IServiceCollection serviceCollection,
																			string jobType )
			where THandler : class, IScheduledJobHandler
		{
			ValidateJobType( jobType );

			GetOrAddRegistrations( serviceCollection ).JobTypes.Add( jobType );

			serviceCollection.AddKeyedScoped<IScheduledJobHandler, THandler>( jobType );

			return serviceCollection;
		}

		/// <summary>
		/// Registers a payload validator for the job type and declares the job type schedulable.
		/// </summary>
		public static IServiceCollection AddJobPayloadValidator<TValidator>( this IServiceCollection serviceCollection,
																			string jobType )
			where TValidator : class, IJobPayloadValidator
		{
			ValidateJobType( jobType );

			GetOrAddRegistrations( serviceCollection ).JobTypes.Add( jobType );

			serviceCollection.AddKeyedSingleton<IJobPayloadValidator, TValidator>( jobType );

			return serviceCollection;
		}

		/// <summary>
		/// Declares a job type schedulable without registering a local handler — for deployments
		/// where execution happens elsewhere (external workers / remote dispatch).
		/// </summary>
		public static IServiceCollection AddSchedulableJobType( this IServiceCollection serviceCollection,
																string jobType )
		{
			ValidateJobType( jobType );

			GetOrAddRegistrations( serviceCollection ).JobTypes.Add( jobType );

			return serviceCollection;
		}

		private static void ValidateJobType( string jobType )
		{
			if( string.IsNullOrWhiteSpace( jobType ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(jobType) );
		}

		private static JobTypeRegistrations GetOrAddRegistrations( IServiceCollection serviceCollection )
		{
			ServiceDescriptor existing = serviceCollection.FirstOrDefault(
					descriptor => descriptor.ServiceType == typeof(JobTypeRegistrations) &&
								descriptor.ImplementationInstance is not null );

			if( existing?.ImplementationInstance is JobTypeRegistrations registrations )
				return registrations;

			JobTypeRegistrations created = new();

			serviceCollection.AddSingleton( created );

			return created;
		}
	}
}
