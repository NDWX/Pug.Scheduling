using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;

namespace Pug.Scheduling.DependencyInjection
{
	public static class IServiceCollectionExtensions
	{
		/// <summary>
		/// Registers the scheduling management facade (<see cref="IScheduler"/>) and execution
		/// coordinator (<see cref="IExecutionCoordinator"/>). The host is expected to have
		/// already registered <see cref="IApplicationData{IDataSession}"/> for the schedule store
		/// (a concrete provider such as the Postgres or SQLite <c>DapperData</c>; execution leases
		/// live on its schedule rows), <see cref="ISecurityManager"/>, and logging
		/// (<see cref="ILogger{TCategoryName}"/>, e.g. via <c>AddLogging()</c>). A registered
		/// <see cref="TimeProvider"/> is used as the facades' clock; otherwise <see cref="TimeProvider.System"/>.
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
							provider.GetRequiredService<IJobTypeRegistry>(),
							options ?? new SchedulerOptions(),
							GetTimeProvider( provider )
						)
				);

			serviceCollection.TryAddSingleton<IExecutionCoordinator>(
					provider => new ExecutionCoordinator(
							identifierGenerator,
							provider.GetRequiredService<IApplicationData<IDataSession>>(),
							provider.GetRequiredService<ISecurityManager>(),
							coordinatorOptions ?? new ExecutionCoordinatorOptions(),
							GetTimeProvider( provider ),
							provider.GetRequiredService<ILogger<ExecutionCoordinator>>()
						)
				);

			return serviceCollection;
		}

		// The host's clock when it registered one (e.g. a fake in tests), else the system clock.
		private static TimeProvider GetTimeProvider( IServiceProvider provider ) =>
			provider.GetService<TimeProvider>() ?? TimeProvider.System;

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
		/// where this host only accepts schedules, e.g. because jobs execute in separate worker hosts, or
		/// to pair with a handler registered through Pug.Scheduling.Worker.DependencyInjection.
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
