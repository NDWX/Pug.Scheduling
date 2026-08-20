using Pug.Application.Data;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Leases.Data.Abstractions
{
	// Data-session contract of the execution-lease store — deliberately separate from the
	// schedule store (its own tables and schema versioning, possibly its own database).
	// Arbitration atomicity lives here: every mutation is a single conditional statement, so
	// concurrent claimants cannot interleave. The current time is always supplied by the caller,
	// keeping expiry comparisons deterministic and testable.
	public interface IDataSession : IApplicationDataSession
	{
		// Inserts the schedule's lease row, or takes it over when the existing lease has expired
		// (expiry <= now) or is already held by claimant (moving its expiry). Returns false —
		// leaving the row untouched — when another claimant holds an unexpired lease.
		Task<bool> TryAcquireAsync( string schedule, string claimant, DateTime expiry, DateTime now );

		// Moves the lease expiry iff claimant holds an unexpired lease on the schedule; returns
		// false otherwise.
		Task<bool> RenewAsync( string schedule, string claimant, DateTime expiry, DateTime now );

		// Removes the lease iff held by claimant; a foreign-held lease is left untouched.
		Task ReleaseAsync( string schedule, string claimant );

		// The schedule's unexpired lease, or null when unheld or expired.
		Task<ExecutionLease?> GetAsync( string schedule, DateTime now );

		// Housekeeping: removes lease rows whose expiry has passed.
		Task PurgeExpiredAsync( DateTime now );
	}
}
