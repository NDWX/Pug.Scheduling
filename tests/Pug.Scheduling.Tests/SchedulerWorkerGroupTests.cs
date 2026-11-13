using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Data.Extensions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Leases;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker;
using Pug.Scheduling.Worker.DependencyInjection;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Worker groups as registered by <c>AddSchedulingWorkers</c>: host-wide numbering, one hosted
/// service per worker, per-group coordinators, and — against the real coordinator over SQLite —
/// concurrent execution across workers and job-type filtering.
/// </summary>
public class SchedulerWorkerGroupTests : IClassFixture<ScheduleDatabaseFixture>, IClassFixture<LeaseDatabaseFixture>
{
	// Records every claim and claims nothing.
	private sealed class RecordingCoordinator : IExecutionCoordinator
	{
		public ConcurrentQueue<(string Claimant, int Worker, string? JobType)> Claims { get; } = new();

		public Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType )
		{
			Claims.Enqueue( ( claimant, worker, jobType ) );

			return Task.FromResult<ClaimedJob?>( null );
		}

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker ) => Task.FromResult( false );

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome ) =>
			Task.CompletedTask;
	}

	private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds( 25 );

	private readonly ScheduleDatabaseFixture _scheduleDatabase;
	private readonly LeaseDatabaseFixture _leaseDatabase;

	public SchedulerWorkerGroupTests( ScheduleDatabaseFixture scheduleDatabase, LeaseDatabaseFixture leaseDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
		_leaseDatabase = leaseDatabase;
	}

	private static ServiceCollection NewServices( IExecutionCoordinator? coordinator = null )
	{
		ServiceCollection services = new();

		services.AddLogging();

		if( coordinator is not null )
			services.AddSingleton( coordinator );

		return services;
	}

	[Fact]
	public void WorkersAreNumberedHostWideInRegistrationOrderAndEachIsAHostedService()
	{
		ServiceCollection services = NewServices( new RecordingCoordinator() );

		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "any-types", Workers = 2 } );
		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "reports", Workers = 3, JobType = "report" } );

		using ServiceProvider provider = services.BuildServiceProvider();

		SchedulerWorkerRegistry registry = provider.GetRequiredService<SchedulerWorkerRegistry>();

		Assert.Same( registry, provider.GetRequiredService<ISchedulerWorkerRegistry>() );

		Assert.Equal(
				new (string, int, string?)[]
				{
					( "any-types", 1, null ), ( "any-types", 2, null ),
					( "reports", 3, "report" ), ( "reports", 4, "report" ), ( "reports", 5, "report" )
				},
				registry.Workers.Select( worker => worker.GetInfo() )
						.Select( info => ( info.Claimant, info.Worker, (string?)info.JobType ) ) );

		// One hosted service per worker — AddHostedService would have deduplicated them to one —
		// each the registry's own instance.
		IHostedService[] hosted = provider.GetServices<IHostedService>().ToArray();

		Assert.Equal( 5, hosted.Length );
		Assert.Equal( 5, hosted.Distinct().Count() );

		for( int number = 1; number <= 5; number++ )
			Assert.Same( registry.Find( number ), hosted[number - 1] );

		Assert.Null( registry.Find( 6 ) );
		Assert.Null( ( (ISchedulerWorkerRegistry)registry ).Find( 0 ) );
	}

	[Fact]
	public void InvalidGroupIsRejectedAtRegistrationWithoutConsumingNumbers()
	{
		ServiceCollection services = NewServices( new RecordingCoordinator() );

		Assert.Throws<ArgumentException>(
				() => services.AddSchedulingWorkers( new WorkerOptions { Claimant = "a/b" } ) );

		Assert.Throws<ArgumentException>(
				() => services.AddSchedulingWorkers( new WorkerOptions { Claimant = "host", Workers = 0 } ) );

		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "host" } );

		using ServiceProvider provider = services.BuildServiceProvider();

		Assert.Equal( 1, Assert.Single( provider.GetRequiredService<ISchedulerWorkerRegistry>().Workers ).GetInfo().Worker );
	}

	[Fact]
	public async Task EveryRegisteredWorkerStartsWithTheHost()
	{
		RecordingCoordinator coordinator = new();

		using IHost host = new HostBuilder()
							.ConfigureServices(
									services =>
									{
										services.AddSingleton<IExecutionCoordinator>( coordinator );

										services.AddSchedulingWorkers(
												new WorkerOptions { Claimant = "any-types", Workers = 2, PollInterval = FastPoll } );

										services.AddSchedulingWorkers(
												new WorkerOptions
												{
													Claimant = "reports", Workers = 2, JobType = "report", PollInterval = FastPoll
												} );
									} )
							.Build();

		await host.StartAsync();

		try
		{
			await WaitForAsync(
					() => Task.FromResult( coordinator.Claims.Select( claim => claim.Worker ).Distinct().Count() == 4 ) );
		}
		finally
		{
			await host.StopAsync();
		}

		Assert.Equal(
				new (string, int, string?)[]
				{
					( "any-types", 1, null ), ( "any-types", 2, null ),
					( "reports", 3, "report" ), ( "reports", 4, "report" )
				},
				coordinator.Claims.Distinct().OrderBy( claim => claim.Worker ) );
	}

	[Fact]
	public async Task GroupCoordinatorFactoryIsResolvedOnceAndUsedByItsWorkersOnly()
	{
		RecordingCoordinator shared = new();
		RecordingCoordinator credentialed = new();

		int factoryCalls = 0;

		using IHost host = new HostBuilder()
							.ConfigureServices(
									services =>
									{
										services.AddSingleton<IExecutionCoordinator>( shared );

										services.AddSchedulingWorkers(
												new WorkerOptions { Claimant = "default-group", PollInterval = FastPoll } );

										services.AddSchedulingWorkers(
												new WorkerOptions
												{
													Claimant = "credentialed-group", Workers = 2, PollInterval = FastPoll
												},
												_ =>
												{
													Interlocked.Increment( ref factoryCalls );

													return credentialed;
												} );
									} )
							.Build();

		await host.StartAsync();

		try
		{
			await WaitForAsync(
					() => Task.FromResult(
							shared.Claims.Any() && credentialed.Claims.Select( claim => claim.Worker ).Distinct().Count() == 2 ) );
		}
		finally
		{
			await host.StopAsync();
		}

		Assert.Equal( 1, factoryCalls );

		Assert.All( shared.Claims, claim => Assert.Equal( ( "default-group", 1 ), ( claim.Claimant, claim.Worker ) ) );

		Assert.All( credentialed.Claims, claim => Assert.Equal( "credentialed-group", claim.Claimant ) );
		Assert.Equal( new[] { 2, 3 }, credentialed.Claims.Select( claim => claim.Worker ).Distinct().Order() );
	}

	[Fact]
	public async Task TwoWorkersRunConcurrentlySoABlockedJobDoesNotHoldBackTheNext()
	{
		string jobType = $"concurrent-{Guid.NewGuid():N}";

		string first = await InsertDueScheduleAsync( jobType );
		string second = await InsertDueScheduleAsync( jobType );

		TaskCompletionSource release = new( TaskCreationOptions.RunContinuationsAsynchronously );
		TaskCompletionSource<string> blocked = new( TaskCreationOptions.RunContinuationsAsynchronously );

		int started = 0;

		// Whichever run starts first blocks until released; the other must still be claimed and
		// completed meanwhile, by the group's other worker.
		DelegateJobHandler handler = new(
				async ( context, _, cancellationToken ) =>
				{
					if( Interlocked.Increment( ref started ) == 1 )
					{
						blocked.TrySetResult( context.Schedule );

						await release.Task.WaitAsync( cancellationToken );
					}

					return new JobOutcome { Successful = true };
				} );

		using IHost host = BuildHost( jobType, handler, new WorkerOptions { Claimant = "concurrent", Workers = 2, JobType = jobType, PollInterval = FastPoll } );

		await host.StartAsync();

		try
		{
			string blockedSchedule = await blocked.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) );
			string otherSchedule = blockedSchedule == first ? second : first;

			// Completes while the first job is still blocked.
			await WaitForAsync( async () => ( await GetRunsAsync( otherSchedule ) ).Any( run => run.Finished is not null ) );

			Assert.All( await GetRunsAsync( blockedSchedule ), run => Assert.Null( run.Finished ) );

			release.TrySetResult();

			await WaitForAsync( async () => ( await GetRunsAsync( blockedSchedule ) ).Any( run => run.Finished is not null ) );
		}
		finally
		{
			release.TrySetResult();

			await host.StopAsync();
		}

		ScheduleRun firstRun = Assert.Single( await GetRunsAsync( first ) );
		ScheduleRun secondRun = Assert.Single( await GetRunsAsync( second ) );

		Assert.Equal( RunResults.Succeeded, firstRun.Result );
		Assert.Equal( RunResults.Succeeded, secondRun.Result );

		// Each held by a different worker of the group.
		Assert.Equal(
				new[] { "concurrent/1", "concurrent/2" },
				new[] { firstRun.ClaimedBy, secondRun.ClaimedBy }.Order() );
	}

	[Fact]
	public async Task GroupWithAJobTypeClaimsOnlyThatType()
	{
		string reportType = $"report-{Guid.NewGuid():N}";
		string otherType = $"other-{Guid.NewGuid():N}";

		string report = await InsertDueScheduleAsync( reportType );
		string other = await InsertDueScheduleAsync( otherType );

		ConcurrentQueue<string> executed = new();

		DelegateJobHandler handler = new(
				( context, _, _ ) =>
				{
					executed.Enqueue( context.JobType );

					return Task.FromResult( new JobOutcome { Successful = true } );
				} );

		// The handler would run either type: only the group's filter keeps the other type out.
		using IHost host = BuildHost(
				null, handler,
				new WorkerOptions { Claimant = "reports-only", JobType = reportType, PollInterval = FastPoll } );

		await host.StartAsync();

		try
		{
			await WaitForAsync( async () => ( await GetRunsAsync( report ) ).Any( run => run.Finished is not null ) );

			// Several more polls, any of which would have claimed the other type unfiltered.
			await Task.Delay( FastPoll * 10 );
		}
		finally
		{
			await host.StopAsync();
		}

		Assert.Equal( new[] { reportType }, executed );
		Assert.Empty( await GetRunsAsync( other ) );
	}

	// A host over the real coordinator, reached in-process (no principal ⇒ no grants needed). A
	// null job type serves the handler for every type.
	private IHost BuildHost( string? handlerJobType, DelegateJobHandler handler, WorkerOptions options ) =>
		new HostBuilder()
			.ConfigureServices(
					services =>
					{
						services.AddSingleton<IExecutionCoordinator>(
								new ExecutionCoordinator(
										new SequentialIdGenerator( $"run-{Guid.NewGuid():N}" ),
										_scheduleDatabase.ApplicationData,
										new DatabaseExecutionLeaseProvider( _leaseDatabase.ApplicationData ),
										SecurityManagerFactory.CreateWithoutPrincipal(),
										new ExecutionCoordinatorOptions(),
										NullLogger<ExecutionCoordinator>.Instance ) );

						DelegateJobHandlerFactory factory = new( _ => handler );

						services.AddSingleton<IScheduledJobHandlerFactoryRegistry>(
								handlerJobType is null
									? new AnyTypeRegistry( factory )
									: new SingleFactoryRegistry( handlerJobType, factory ) );

						services.AddSchedulingWorkers( options );
					} )
			.Build();

	private sealed class AnyTypeRegistry : IScheduledJobHandlerFactoryRegistry
	{
		private readonly IScheduledJobHandlerFactory _factory;

		public AnyTypeRegistry( IScheduledJobHandlerFactory factory )
		{
			_factory = factory;
		}

		public IScheduledJobHandlerFactory GetFactory( string jobType ) => _factory;
	}

	private async Task<string> InsertDueScheduleAsync( string jobType )
	{
		ScheduleInfo schedule = new()
		{
			Identifier = $"schedule-{Guid.NewGuid():N}",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			Definition = new ScheduleDefinition
			{
				Domain = "tenant-1",
				Name = "Worker group test schedule",
				JobType = jobType,
				Payload = /*lang=json*/ """{"configuration":"report-1"}""",
				Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" },
				MisfirePolicy = new MisfirePolicy
				{
					Handling = MisfireHandlings.FireOnce,
					Threshold = TimeSpan.FromMinutes( 5 )
				},
				RetryPolicy = new RetryPolicy { MaximumAttempts = 3, Backoff = TimeSpan.FromMinutes( 1 ) },
				Timeout = TimeSpan.FromMinutes( 10 )
			},
			State = new ScheduleState
			{
				Enabled = true,
				Paused = false,
				NextRun = DateTime.UtcNow.AddSeconds( -1 ),
				Attempt = 0
			},
			Registration = new ActionContext<Reference>
			{
				Actor = new Reference { Type = "USER", Identifier = "owner-1" },
				Timestamp = DateTime.UtcNow
			}
		};

		await _scheduleDatabase.ApplicationData.PerformAsync(
				async ( session, context ) => await session.InsertAsync( context.schedule ),
				context: new { schedule } );

		return schedule.Identifier;
	}

	private Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.ExecuteAsync(
				async ( session, context ) => await session.GetRunsAsync( context.schedule ),
				context: new { schedule } );

	private static async Task WaitForAsync( Func<Task<bool>> condition )
	{
		DateTime deadline = DateTime.UtcNow.AddSeconds( 10 );

		while( DateTime.UtcNow < deadline )
		{
			if( await condition() )
				return;

			await Task.Delay( 25 );
		}

		throw new TimeoutException( "Condition was not met before the deadline." );
	}
}
