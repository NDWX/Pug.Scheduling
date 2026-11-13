using Microsoft.Extensions.DependencyInjection;
using Pug.Effable;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Worker.DependencyInjection;

namespace Pug.Scheduling.Tests;

// Handler registration in the worker host's DI: factory registry lookup and the one-scope-per-run
// lifetime of handlers registered through AddScheduledJobHandler.
public class JobHandlerRegistrationTests
{
	private sealed class ScopedDependency : IDisposable
	{
		public int Disposals { get; private set; }

		public void Dispose() => Disposals++;
	}

	// Records the scoped dependency each handler instance received.
	private sealed class Seen
	{
		public List<ScopedDependency> Dependencies { get; } = new();
	}

	private sealed class ScopedDependencyHandler : IScheduledJobHandler
	{
		public ScopedDependencyHandler( ScopedDependency dependency, Seen seen )
		{
			seen.Dependencies.Add( dependency );
		}

		public Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout,
											CancellationToken cancellationToken ) =>
			Task.FromResult( new JobOutcome { Successful = true } );
	}

	private static readonly ScheduledJobContext Context = new()
	{
		Schedule = "schedule-1",
		Run = "run-1",
		Domain = "tenant-1",
		Owner = new Reference { Type = "USER", Identifier = "owner-1" },
		JobType = "report",
		ScheduledFor = DateTime.UtcNow,
		Attempt = 0,
		Trigger = RunTriggers.Schedule
	};

	private static ServiceProvider BuildProvider( Seen seen )
	{
		ServiceCollection services = new();

		services.AddSingleton( seen );
		services.AddScoped<ScopedDependency>();
		services.AddScheduledJobHandler<ScopedDependencyHandler>( "report" );
		services.AddSchedulingWorker();

		return services.BuildServiceProvider();
	}

	[Fact]
	public async Task EachRunGetsItsOwnScopeDisposedWithTheHandler()
	{
		Seen seen = new();

		await using ServiceProvider provider = BuildProvider( seen );

		IScheduledJobHandlerFactoryRegistry registry =
			provider.GetRequiredService<IScheduledJobHandlerFactoryRegistry>();

		IScheduledJobHandlerFactory factory = registry.GetFactory( "report" );

		Assert.NotNull( factory );

		IScheduledJobHandler first = factory.Create( Context );
		IScheduledJobHandler second = factory.Create( Context );

		Assert.Equal( 2, seen.Dependencies.Count );

		ScopedDependency firstDependency = seen.Dependencies[0];
		ScopedDependency secondDependency = seen.Dependencies[1];

		Assert.NotSame( firstDependency, secondDependency );
		Assert.Equal( 0, firstDependency.Disposals );

		await ( (IAsyncDisposable)first ).DisposeAsync();

		Assert.Equal( 1, firstDependency.Disposals );
		Assert.Equal( 0, secondDependency.Disposals );

		await ( (IAsyncDisposable)second ).DisposeAsync();

		Assert.Equal( 1, secondDependency.Disposals );
	}

	[Fact]
	public void UnregisteredJobTypeHasNoFactory()
	{
		using ServiceProvider provider = BuildProvider( new Seen() );

		Assert.Null( provider.GetRequiredService<IScheduledJobHandlerFactoryRegistry>().GetFactory( "other" ) );
	}

	[Fact]
	public void BlankJobTypeIsRejected()
	{
		Assert.Throws<ArgumentException>(
				() => new ServiceCollection().AddScheduledJobHandler<ScopedDependencyHandler>( " " ) );
	}
}
