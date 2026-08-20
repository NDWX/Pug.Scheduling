using Pug.Application.Data;
using Pug.Application.Data.Extensions;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Leases.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Leases
{
	// Database-backed IExecutionLeaseProvider. Arbitration is delegated to single-statement
	// conditional writes in the lease store — the store's atomicity is what guarantees
	// exclusivity, so any number of coordinator instances may race safely. The lease store is
	// the provider's own (its own tables, possibly its own database); lease state never touches
	// the schedule store. All times are UTC.
	public class DatabaseExecutionLeaseProvider : IExecutionLeaseProvider
	{
		private readonly IApplicationData<IDataSession> applicationDataProvider;

		public DatabaseExecutionLeaseProvider( IApplicationData<IDataSession> applicationData )
		{
			applicationDataProvider = applicationData ??
									throw new ArgumentNullException( nameof(applicationData) );
		}

		private static void ValidateArguments( string schedule, string claimant )
		{
			ValidateSchedule( schedule );

			if( string.IsNullOrWhiteSpace( claimant ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(claimant) );
		}

		private static void ValidateSchedule( string schedule )
		{
			if( string.IsNullOrWhiteSpace( schedule ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(schedule) );
		}

		public async Task<bool> TryAcquireAsync( string schedule, string claimant, DateTime expiry )
		{
			ValidateArguments( schedule, claimant );

			return await applicationDataProvider.ExecuteAsync(
						async ( session, context ) =>
							await session.TryAcquireAsync(
											context.schedule, context.claimant, context.expiry,
											DateTime.UtcNow
										)
										.ConfigureAwait( false ),
						context: new { schedule, claimant, expiry }
					).ConfigureAwait( false );
		}

		public async Task<bool> RenewAsync( string schedule, string claimant, DateTime expiry )
		{
			ValidateArguments( schedule, claimant );

			return await applicationDataProvider.ExecuteAsync(
						async ( session, context ) =>
							await session.RenewAsync(
											context.schedule, context.claimant, context.expiry,
											DateTime.UtcNow
										)
										.ConfigureAwait( false ),
						context: new { schedule, claimant, expiry }
					).ConfigureAwait( false );
		}

		public async Task ReleaseAsync( string schedule, string claimant )
		{
			ValidateArguments( schedule, claimant );

			await applicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.ReleaseAsync( context.schedule, context.claimant )
										.ConfigureAwait( false ),
						context: new { schedule, claimant }
					).ConfigureAwait( false );
		}

		public async Task<ExecutionLease> GetAsync( string schedule )
		{
			ValidateSchedule( schedule );

			return await applicationDataProvider.ExecuteAsync(
						async ( session, context ) =>
							await session.GetAsync( context.schedule, DateTime.UtcNow )
										.ConfigureAwait( false ),
						context: new { schedule }
					).ConfigureAwait( false );
		}
	}
}
