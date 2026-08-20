using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.DependencyInjection
{
	// Default dispatcher: resolves the IScheduledJobHandler registered (keyed) for the job type
	// from a dedicated dependency-injection scope per run, and invokes it directly. Swapping this
	// for a remote transport (e.g. a webhook dispatcher) touches neither the worker loop nor
	// handler contracts.
	public sealed class InProcessJobDispatcher : IScheduledJobDispatcher
	{
		private readonly IServiceScopeFactory _scopeFactory;

		public InProcessJobDispatcher( IServiceScopeFactory scopeFactory )
		{
			_scopeFactory = scopeFactory ?? throw new ArgumentNullException( nameof(scopeFactory) );
		}

		public async Task<JobOutcome> DispatchAsync( ScheduledJobContext context, CancellationToken cancellationToken )
		{
			if( context == null ) throw new ArgumentNullException( nameof(context) );

			using IServiceScope scope = _scopeFactory.CreateScope();

			IScheduledJobHandler handler =
				scope.ServiceProvider.GetRequiredKeyedService<IScheduledJobHandler>( context.JobType );

			return await handler.ExecuteAsync( context, cancellationToken ).ConfigureAwait( false );
		}
	}
}
