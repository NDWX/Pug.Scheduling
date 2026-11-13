using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.JobHandler.Abstractions;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	// Resolves the factory registered (keyed) for the job type, or null.
	internal sealed class JobHandlerFactoryRegistry : IScheduledJobHandlerFactoryRegistry
	{
		private readonly IServiceProvider _serviceProvider;

		public JobHandlerFactoryRegistry( IServiceProvider serviceProvider )
		{
			_serviceProvider = serviceProvider ?? throw new ArgumentNullException( nameof(serviceProvider) );
		}

		public IScheduledJobHandlerFactory GetFactory( string jobType ) =>
			_serviceProvider.GetKeyedService<IScheduledJobHandlerFactory>( jobType );
	}
}
