using System.Data;
using Dapper;
using Pug.Application.Data;
using Pug.Scheduling.Leases.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Leases.Data.SQLite
{
	public class DapperDataSession : ApplicationDataSession, IDataSession
	{
		public DapperDataSession( IDbConnection databaseSession ) : base( databaseSession )
		{
		}

		public async Task<bool> TryAcquireAsync( string schedule, string claimant, DateTime expiry, DateTime now )
		{
			// Atomic arbitration: the insert either creates the lease, or — on conflict — takes
			// over only an expired or own lease; a live foreign lease leaves the row untouched
			// and affects no rows.
			int affected = await Connection.ExecuteAsync(
								@"insert into executionLease(schedule, claimedBy, expiry) values(@schedule, @claimant, @expiry)
									on conflict(schedule) do update set claimedBy = excluded.claimedBy, expiry = excluded.expiry
									where executionLease.expiry <= @now or executionLease.claimedBy = excluded.claimedBy;",
								new { schedule, claimant, expiry, now }
							).ConfigureAwait( false );

			return affected > 0;
		}

		public async Task<bool> RenewAsync( string schedule, string claimant, DateTime expiry, DateTime now )
		{
			int affected = await Connection.ExecuteAsync(
								@"update executionLease set expiry = @expiry
									where schedule = @schedule and claimedBy = @claimant and expiry > @now;",
								new { schedule, claimant, expiry, now }
							).ConfigureAwait( false );

			return affected > 0;
		}

		public Task ReleaseAsync( string schedule, string claimant )
		{
			return Connection.ExecuteAsync(
					"delete from executionLease where schedule = @schedule and claimedBy = @claimant;",
					new { schedule, claimant }
				);
		}

		public Task<ExecutionLease?> GetAsync( string schedule, DateTime now )
		{
			return Connection.QueryFirstOrDefaultAsync<ExecutionLease?>(
					@"select claimedBy as ClaimedBy, expiry as Expiry from executionLease
						where schedule = @schedule and expiry > @now;",
					new { schedule, now }
				);
		}

		public Task PurgeExpiredAsync( DateTime now )
		{
			return Connection.ExecuteAsync(
					"delete from executionLease where expiry <= @now;",
					new { now }
				);
		}
	}
}
