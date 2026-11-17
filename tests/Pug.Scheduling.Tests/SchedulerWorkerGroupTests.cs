using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Pug.Application.Security;
using Pug.Scheduling.Data.Abstractions;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker;
using Pug.Scheduling.Worker.DependencyInjection;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Worker groups as registered by <c>AddSchedulingWorkers</c>: host-wide numbering, one hosted
/// service per worker, per-group coordinators, and — against the real coordinator over each
/// provider's store, per the subclasses below — concurrent execution across workers and job-type
/// filtering.
/// </summary>
// The fixture is declared by the concrete subclasses.
public abstract class SchedulerWorkerGroupTests<TScheduleDatabase>
	where TScheduleDatabase : class, IScheduleDatabaseFixture
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

		public Task<bool> AbandonClaimAsync( string run, string claimant, int worker ) => Task.FromResult( false );

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome ) =>
			Task.CompletedTask;
	}

	// The real coordinator, refusing completion reports while RefusesCompletion is set, as one
	// whose worker lacks REPORT_COMPLETION would.
	private sealed class CompletionRefusingCoordinator( IExecutionCoordinator inner ) : IExecutionCoordinator
	{
		public volatile bool RefusesCompletion = true;

		private int _refusedReports;

		public int RefusedReports => Volatile.Read( ref _refusedReports );

		private int _claimed;

		// Claims that handed out a job.
		public int Claimed => Volatile.Read( ref _claimed );

		public async Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType )
		{
			ClaimedJob? job = await inner.ClaimDueJobAsync( claimant, worker, jobType! );

			if( job is not null )
				Interlocked.Increment( ref _claimed );

			return job;
		}

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker ) =>
			inner.RenewClaimAsync( run, claimant, worker );

		public Task<bool> AbandonClaimAsync( string run, string claimant, int worker ) =>
			inner.AbandonClaimAsync( run, claimant, worker );

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			if( !RefusesCompletion )
				return inner.ReportCompletionAsync( run, claimant, worker, result, outcome );

			Interlocked.Increment( ref _refusedReports );

			return Task.FromException( new NotAuthorized() );
		}
	}

	// Offers one occurrence to whichever claimant it is told to, until a report of it is recorded,
	// and answers "not held" to reports from the claimant it refuses — so that claimant drops (and
	// remembers) its success at once.
	private sealed class OneOccurrenceCoordinator( ClaimedJob job, string refusedReporter ) : IExecutionCoordinator
	{
		public volatile string? OfferTo;

		private int _reportAttempts;

		public int ReportAttempts => Volatile.Read( ref _reportAttempts );

		public TaskCompletionSource<string> Recorded { get; } = new( TaskCreationOptions.RunContinuationsAsynchronously );

		public Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType ) =>
			Task.FromResult( claimant == OfferTo && !Recorded.Task.IsCompleted ? job : null );

		public Task<bool> RenewClaimAsync( string run, string claimant, int worker ) => Task.FromResult( true );

		public Task<bool> AbandonClaimAsync( string run, string claimant, int worker ) => Task.FromResult( true );

		public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			Interlocked.Increment( ref _reportAttempts );

			if( claimant == refusedReporter )
				return Task.FromException( new ClaimNotHeldException( run ) );

			Recorded.TrySetResult( claimant );

			return Task.CompletedTask;
		}
	}

	private static readonly TimeSpan FastPoll = TimeSpan.FromMilliseconds( 25 );

	private readonly TScheduleDatabase _scheduleDatabase;

	protected SchedulerWorkerGroupTests( TScheduleDatabase scheduleDatabase )
	{
		_scheduleDatabase = scheduleDatabase;
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

	[Fact]
	public async Task ARefusedCompletionIsRetriedWithoutPausingAndRecordedOnceGranted()
	{
		string jobType = $"refused-{Guid.NewGuid():N}";

		string schedule = await InsertDueScheduleAsync( jobType );

		int executions = 0;

		DelegateJobHandler handler = new(
				( _, _, _ ) =>
				{
					Interlocked.Increment( ref executions );

					return Task.FromResult( new JobOutcome { Successful = true } );
				} );

		CompletionRefusingCoordinator coordinator = new( NewCoordinator() );

		using IHost host = BuildHost(
				jobType, handler,
				new WorkerOptions { Claimant = "refused", JobType = jobType, PollInterval = FastPoll },
				coordinator );

		ISchedulerWorker worker = Assert.Single( host.Services.GetRequiredService<ISchedulerWorkerRegistry>().Workers );

		await host.StartAsync();

		try
		{
			// Retried every PollInterval within the 2-minute lease, never paused, never released.
			await WaitForAsync( () => Task.FromResult( coordinator.RefusedReports >= 3 ) );

			Assert.False( worker.GetInfo().Paused );
			Assert.Null( worker.GetInfo().Fault );
			Assert.Equal( 1, executions );

			ScheduleRun pending = Assert.Single( await GetRunsAsync( schedule ) );

			Assert.Null( pending.Finished );
			Assert.Equal( 0, pending.Attempt );

			// The grant is restored (or the security provider recovers): the next retry records
			// the same run, executed once.
			coordinator.RefusesCompletion = false;

			await WaitForAsync( async () => ( await GetRunsAsync( schedule ) ).Any( run => run.Finished is not null ) );

			ScheduleRun completed = Assert.Single( await GetRunsAsync( schedule ) );

			Assert.Equal( pending.Identifier, completed.Identifier );
			Assert.Equal( RunResults.Succeeded, completed.Result );
			Assert.Equal( 0, completed.Attempt );
			Assert.Equal( 1, executions );
			Assert.False( worker.GetInfo().Paused );
		}
		finally
		{
			await host.StopAsync();
		}
	}

	[Fact]
	public async Task ASuccessRefusedPastTheLeaseIsReplayedOnReadoptionAndRecordedOnceGranted()
	{
		string jobType = $"replayed-{Guid.NewGuid():N}";

		string schedule = await InsertDueScheduleAsync( jobType );

		int executions = 0;

		DelegateJobHandler handler = new(
				( _, _, _ ) =>
				{
					Interlocked.Increment( ref executions );

					return Task.FromResult( new JobOutcome { Successful = true } );
				} );

		// The shortest lease the coordinator accepts, so the report's window (9.5 s) runs out.
		CompletionRefusingCoordinator coordinator = new( NewCoordinator( ExecutionCoordinatorOptions.MinimumLeaseDuration ) );

		using IHost host = BuildHost(
				jobType, handler,
				new WorkerOptions { Claimant = "replayed", JobType = jobType, PollInterval = FastPoll },
				coordinator );

		await host.StartAsync();

		try
		{
			// The report is dropped at the lease deadline and the success remembered; the next claim
			// adopts the same run (the row still names this worker as holder) and replays it.
			await WaitForAsync( () => Task.FromResult( coordinator.Claimed >= 2 ), TimeSpan.FromSeconds( 30 ) );

			Assert.Equal( 1, executions );

			ScheduleRun pending = Assert.Single( await GetRunsAsync( schedule ) );

			Assert.Null( pending.Finished );
			Assert.Equal( 0, pending.Attempt );

			// The grant is restored: the replay's retry records the run.
			coordinator.RefusesCompletion = false;

			await WaitForAsync( async () => ( await GetRunsAsync( schedule ) ).Any( run => run.Finished is not null ) );

			ScheduleRun completed = Assert.Single( await GetRunsAsync( schedule ) );

			Assert.Equal( pending.Identifier, completed.Identifier );
			Assert.Equal( RunResults.Succeeded, completed.Result );
			Assert.Equal( 0, completed.Attempt );
			Assert.Equal( 1, executions );
		}
		finally
		{
			await host.StopAsync();
		}
	}

	[Fact]
	public async Task GroupsOnTheSameCoordinatorShareOneReplayStoreAndAGroupWithItsOwnDoesNot()
	{
		ScheduledJobContext occurrence = new()
		{
			Schedule = "schedule-1",
			Run = "run-1",
			Domain = "tenant-1",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			JobType = "test",
			ScheduledFor = new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc ),
			Attempt = 0,
			Trigger = RunTriggers.Schedule
		};

		ClaimedJob job = new()
		{
			Context = occurrence,
			Timeout = TimeSpan.FromMinutes( 1 ),
			ClaimExpiry = DateTime.UtcNow.AddMinutes( 2 ),
			ClaimDuration = TimeSpan.FromMinutes( 2 )
		};

		// The container's coordinator: group "first" has its report refused as not held, and
		// remembers the success; group "second" then claims the same occurrence. Group "own" claims
		// the same occurrence through its own coordinator.
		OneOccurrenceCoordinator shared = new( job, refusedReporter: "first" ) { OfferTo = "first" };
		OneOccurrenceCoordinator own = new( job, refusedReporter: "nobody" );

		int executions = 0;

		DelegateJobHandler handler = new(
				( _, _, _ ) =>
				{
					Interlocked.Increment( ref executions );

					return Task.FromResult( new JobOutcome { Successful = true } );
				} );

		ServiceCollection services = NewServices( shared );

		services.AddSingleton<IScheduledJobHandlerFactoryRegistry>( new AnyTypeRegistry( new DelegateJobHandlerFactory( _ => handler ) ) );

		// A long PollInterval: after its unrecorded run, "first" does not claim again during the test.
		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "first", PollInterval = TimeSpan.FromMinutes( 1 ) } );
		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "second", PollInterval = FastPoll } );
		services.AddSchedulingWorkers( new WorkerOptions { Claimant = "own", PollInterval = FastPoll }, _ => own );

		await using ServiceProvider provider = services.BuildServiceProvider();

		SchedulerWorkerRegistry registry = provider.GetRequiredService<SchedulerWorkerRegistry>();

		SchedulerWorker first = registry.Find( 1 ), second = registry.Find( 2 ), separate = registry.Find( 3 );

		await first.StartAsync( CancellationToken.None );

		try
		{
			// Settled once the run is no longer attended: the success is remembered by then.
			await WaitForAsync( () => Task.FromResult( shared.ReportAttempts == 1 && first.GetInfo().ActiveJobs == 0 ) );

			Assert.Equal( 1, executions );

			// Another store: "own" runs the occurrence.
			own.OfferTo = "own";

			await separate.StartAsync( CancellationToken.None );

			Assert.Equal( "own", await own.Recorded.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) ) );
			Assert.Equal( 2, executions );

			// The same store: "second" replays it.
			shared.OfferTo = "second";

			await second.StartAsync( CancellationToken.None );

			Assert.Equal( "second", await shared.Recorded.Task.WaitAsync( TimeSpan.FromSeconds( 10 ) ) );
			Assert.Equal( 2, executions );
		}
		finally
		{
			await Task.WhenAll( first.StopAsync( CancellationToken.None ), second.StopAsync( CancellationToken.None ),
								separate.StopAsync( CancellationToken.None ) );
		}
	}

	// The real coordinator, reached in-process (no principal ⇒ no grants needed).
	private ExecutionCoordinator NewCoordinator( TimeSpan? leaseDuration = null ) =>
		new(
				new SequentialIdGenerator( $"run-{Guid.NewGuid():N}" ),
				_scheduleDatabase.ApplicationData,
				SecurityManagerFactory.CreateWithoutPrincipal(),
				new ExecutionCoordinatorOptions { LeaseDuration = leaseDuration ?? TimeSpan.FromMinutes( 2 ) },
				new TimeProviderClock( TimeProvider.System ),
				NullLogger<ExecutionCoordinator>.Instance );

	// A host over the given coordinator, or the real one. A null job type serves the handler for
	// every type.
	private IHost BuildHost( string? handlerJobType, DelegateJobHandler handler, WorkerOptions options,
							IExecutionCoordinator? coordinator = null ) =>
		new HostBuilder()
			.ConfigureServices(
					services =>
					{
						services.AddSingleton( coordinator ?? NewCoordinator() );

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

		await _scheduleDatabase.ApplicationData.WriteAsync(
				( session, context ) => session.InsertAsync( context ), schedule );

		return schedule.Identifier;
	}

	private Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.ReadAsync(
				( session, context ) => session.GetRunsAsync( context ), schedule );

	private static async Task WaitForAsync( Func<Task<bool>> condition, TimeSpan? within = null )
	{
		DateTime deadline = DateTime.UtcNow + ( within ?? TimeSpan.FromSeconds( 10 ) );

		while( DateTime.UtcNow < deadline )
		{
			if( await condition() )
				return;

			await Task.Delay( 25 );
		}

		throw new TimeoutException( "Condition was not met before the deadline." );
	}
}

public sealed class SqliteSchedulerWorkerGroupTests
	: SchedulerWorkerGroupTests<SqliteScheduleDatabaseFixture>, IClassFixture<SqliteScheduleDatabaseFixture>
{
	public SqliteSchedulerWorkerGroupTests( SqliteScheduleDatabaseFixture scheduleDatabase )
		: base( scheduleDatabase )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresSchedulerWorkerGroupTests
	: SchedulerWorkerGroupTests<PostgresScheduleDatabaseFixture>, IClassFixture<PostgresScheduleDatabaseFixture>
{
	public PostgresSchedulerWorkerGroupTests( PostgresScheduleDatabaseFixture scheduleDatabase )
		: base( scheduleDatabase )
	{
	}
}
