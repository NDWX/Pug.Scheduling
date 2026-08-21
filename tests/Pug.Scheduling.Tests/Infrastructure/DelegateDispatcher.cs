using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Dispatcher that hands each claimed job to a supplied delegate — the job handler stand-in for
/// worker tests.
/// </summary>
internal sealed class DelegateDispatcher : IScheduledJobDispatcher
{
	private readonly Func<ScheduledJobContext, CancellationToken, Task<JobOutcome>> _dispatch;

	internal DelegateDispatcher( Func<ScheduledJobContext, CancellationToken, Task<JobOutcome>> dispatch )
	{
		_dispatch = dispatch;
	}

	public Task<JobOutcome> DispatchAsync( ScheduledJobContext context, CancellationToken cancellationToken ) =>
		_dispatch( context, cancellationToken );
}
