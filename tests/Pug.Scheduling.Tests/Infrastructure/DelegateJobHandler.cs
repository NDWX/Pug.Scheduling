using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Handler that runs a supplied delegate for each claimed job and counts its disposals — the job
/// handler stand-in for worker tests.
/// </summary>
internal sealed class DelegateJobHandler : IScheduledJobHandler, IAsyncDisposable
{
	private readonly Func<ScheduledJobContext, TimeSpan, CancellationToken, Task<JobOutcome>> _execute;
	private int _disposals;

	internal DelegateJobHandler( Func<ScheduledJobContext, TimeSpan, CancellationToken, Task<JobOutcome>> execute )
	{
		_execute = execute;
	}

	internal int Disposals => Volatile.Read( ref _disposals );

	public Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout,
										CancellationToken cancellationToken ) =>
		_execute( context, timeout, cancellationToken );

	public ValueTask DisposeAsync()
	{
		Interlocked.Increment( ref _disposals );

		return ValueTask.CompletedTask;
	}
}

/// <summary>
/// Factory returning a supplied handler (or whatever a delegate produces) for any context.
/// </summary>
internal sealed class DelegateJobHandlerFactory : IScheduledJobHandlerFactory
{
	private readonly Func<ScheduledJobContext, IScheduledJobHandler?> _create;

	internal DelegateJobHandlerFactory( Func<ScheduledJobContext, IScheduledJobHandler?> create )
	{
		_create = create;
	}

	public IScheduledJobHandler Create( ScheduledJobContext context ) => _create( context )!;
}

/// <summary>
/// Registry serving one factory for one job type; every other job type has none.
/// </summary>
internal sealed class SingleFactoryRegistry : IScheduledJobHandlerFactoryRegistry
{
	private readonly string _jobType;
	private readonly IScheduledJobHandlerFactory _factory;

	internal SingleFactoryRegistry( string jobType, IScheduledJobHandlerFactory factory )
	{
		_jobType = jobType;
		_factory = factory;
	}

	internal static SingleFactoryRegistry For( string jobType, DelegateJobHandler handler ) =>
		new( jobType, new DelegateJobHandlerFactory( _ => handler ) );

	public IScheduledJobHandlerFactory GetFactory( string jobType ) =>
		jobType == _jobType ? _factory : null!;
}
