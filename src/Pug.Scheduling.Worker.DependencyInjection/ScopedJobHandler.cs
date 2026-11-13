using Microsoft.Extensions.DependencyInjection;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Worker.DependencyInjection
{
	// Delegates to a handler resolved from a dependency-injection scope and owns that scope:
	// disposing the wrapper disposes the scope (and with it the handler and its scoped
	// dependencies).
	internal sealed class ScopedJobHandler : IScheduledJobHandler, IAsyncDisposable
	{
		private readonly IScheduledJobHandler _handler;
		private readonly AsyncServiceScope _scope;

		public ScopedJobHandler( IScheduledJobHandler handler, AsyncServiceScope scope )
		{
			_handler = handler;
			_scope = scope;
		}

		public Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout,
											CancellationToken cancellationToken ) =>
			_handler.ExecuteAsync( context, timeout, cancellationToken );

		public ValueTask DisposeAsync() => _scope.DisposeAsync();
	}
}
