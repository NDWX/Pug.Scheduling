using Pug.Application.Data.Extensions;
using Pug.Scheduling.Leases;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;

namespace Pug.Scheduling.Tests;

// Arbitration semantics of the database-backed execution-lease provider: the behavior contract of
// every lease store, run against each provider's store by the subclasses below (Postgres in a
// Testcontainers database, so the concurrent-acquisition race meets real row locking).
public abstract class ExecutionLeaseProviderTests<TLeaseDatabase> : IClassFixture<TLeaseDatabase>
	where TLeaseDatabase : class, ILeaseDatabaseFixture
{
	private readonly TLeaseDatabase _fixture;
	private readonly DatabaseExecutionLeaseProvider _provider;

	private static readonly DateTime FutureExpiry = DateTime.UtcNow.AddMinutes( 10 );
	private static readonly DateTime FartherFutureExpiry = DateTime.UtcNow.AddMinutes( 20 );
	private static readonly DateTime PastExpiry = DateTime.UtcNow.AddMinutes( -10 );

	protected ExecutionLeaseProviderTests( TLeaseDatabase fixture )
	{
		_fixture = fixture;
		_provider = new DatabaseExecutionLeaseProvider( fixture.ApplicationData );
	}

	private static string NewSchedule() => $"schedule-{Guid.NewGuid():N}";

	[Fact]
	public async Task AcquisitionOfUnheldLeaseSucceeds()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.NotNull( lease );
		Assert.Equal( "worker-1", lease.ClaimedBy );
	}

	[Fact]
	public async Task AcquisitionAgainstLiveForeignLeaseFailsAndLeavesLeaseIntact()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );
		Assert.False( await _provider.TryAcquireAsync( schedule, "worker-2", FartherFutureExpiry ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.Equal( "worker-1", lease.ClaimedBy );
	}

	[Fact]
	public async Task ReacquisitionByHolderMovesExpiry()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );
		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FartherFutureExpiry ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.Equal( "worker-1", lease.ClaimedBy );
		Assert.True( lease.Expiry > FutureExpiry );
	}

	[Fact]
	public async Task ExpiredForeignLeaseIsClaimable()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", PastExpiry ) );
		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-2", FutureExpiry ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.Equal( "worker-2", lease.ClaimedBy );
	}

	[Fact]
	public async Task RenewalByHolderMovesExpiry()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );
		Assert.True( await _provider.RenewAsync( schedule, "worker-1", FartherFutureExpiry ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.True( lease.Expiry > FutureExpiry );
	}

	[Fact]
	public async Task RenewalByNonHolderFails()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );
		Assert.False( await _provider.RenewAsync( schedule, "worker-2", FartherFutureExpiry ) );
	}

	[Fact]
	public async Task RenewalOfLapsedLeaseFails()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", PastExpiry ) );
		Assert.False( await _provider.RenewAsync( schedule, "worker-1", FutureExpiry ) );
	}

	[Fact]
	public async Task ReleaseByHolderFreesLease()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );

		await _provider.ReleaseAsync( schedule, "worker-1" );

		Assert.Null( await _provider.GetAsync( schedule ) );
		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-2", FutureExpiry ) );
	}

	[Fact]
	public async Task ReleaseByNonHolderLeavesLeaseHeld()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", FutureExpiry ) );

		await _provider.ReleaseAsync( schedule, "worker-2" );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.NotNull( lease );
		Assert.Equal( "worker-1", lease.ClaimedBy );
	}

	[Fact]
	public async Task ExpiredLeaseReadsAsUnheld()
	{
		string schedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( schedule, "worker-1", PastExpiry ) );

		Assert.Null( await _provider.GetAsync( schedule ) );
	}

	[Fact]
	public async Task PurgeRemovesOnlyExpiredLeases()
	{
		string expiredSchedule = NewSchedule();
		string liveSchedule = NewSchedule();

		Assert.True( await _provider.TryAcquireAsync( expiredSchedule, "worker-1", PastExpiry ) );
		Assert.True( await _provider.TryAcquireAsync( liveSchedule, "worker-1", FutureExpiry ) );

		await _fixture.ApplicationData.PerformAsync(
				async ( session, context ) => await session.PurgeExpiredAsync( context.now ),
				context: new { now = DateTime.UtcNow }
			);

		Assert.Null( await _provider.GetAsync( expiredSchedule ) );
		Assert.NotNull( await _provider.GetAsync( liveSchedule ) );
	}

	[Fact]
	public async Task ConcurrentAcquisitionAdmitsExactlyOneClaimant()
	{
		string schedule = NewSchedule();

		// Released together from pool threads: started inline, the attempts would run one after
		// another up to their first real await (the session opens its connection synchronously).
		TaskCompletionSource start = new( TaskCreationOptions.RunContinuationsAsynchronously );

		Task<bool>[] attempts =
			Enumerable.Range( 0, 8 )
					.Select(
							worker => Task.Run(
									async () =>
									{
										await start.Task;

										return await _provider.TryAcquireAsync( schedule, $"worker-{worker}", FutureExpiry );
									} ) )
					.ToArray();

		start.SetResult();

		bool[] results = await Task.WhenAll( attempts );

		Assert.Equal( 1, results.Count( claimed => claimed ) );

		ExecutionLease lease = await _provider.GetAsync( schedule );

		Assert.NotNull( lease );
	}
}

public sealed class SqliteExecutionLeaseProviderTests : ExecutionLeaseProviderTests<SqliteLeaseDatabaseFixture>
{
	public SqliteExecutionLeaseProviderTests( SqliteLeaseDatabaseFixture fixture ) : base( fixture )
	{
	}
}

[Trait( "Category", "Postgres" )]
public sealed class PostgresExecutionLeaseProviderTests : ExecutionLeaseProviderTests<PostgresLeaseDatabaseFixture>
{
	public PostgresExecutionLeaseProviderTests( PostgresLeaseDatabaseFixture fixture ) : base( fixture )
	{
	}
}
