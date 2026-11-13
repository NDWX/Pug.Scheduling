using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	// Creates the handler registered (keyed, scoped) for the job type from a dedicated
	// dependency-injection scope per run, so handlers may take scoped dependencies. The scope
	// lives until the returned handler is disposed.
	internal sealed class ServiceProviderJobHandlerFactory : IScheduledJobHandlerFactory
	{
		private readonly IServiceScopeFactory _scopeFactory;
		private readonly string _jobType;

		public ServiceProviderJobHandlerFactory( IServiceScopeFactory scopeFactory, string jobType )
		{
			_scopeFactory = scopeFactory ?? throw new ArgumentNullException( nameof(scopeFactory) );
			_jobType = jobType;
		}

		public IScheduledJobHandler Create( ScheduledJobContext context )
		{
			AsyncServiceScope scope = _scopeFactory.CreateAsyncScope();

			try
			{
				return new ScopedJobHandler(
						scope.ServiceProvider.GetRequiredKeyedService<IScheduledJobHandler>( _jobType ), scope );
			}
			catch
			{
				scope.Dispose();

				throw;
			}
		}
	}
}
