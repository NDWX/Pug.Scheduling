using System;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Arbitration of exclusive execution leases, used by the execution coordinator to guarantee
	// that at most one worker executes a schedule's due occurrence at a time. The claim
	// implementation — in-memory, database-backed (in the schedule store's database or its own),
	// or remote (e.g. a distributed lock service) — is transparent to the coordinator, and lease
	// state is never stored with the schedule itself. Implementations must arbitrate atomically
	// under concurrent callers, and must treat an expired lease as not held.
	public interface IExecutionLeaseProvider
	{
		// Attempts to acquire an exclusive execution lease on the schedule for claimant, valid
		// until expiry (UTC). Returns false when another claimant holds an unexpired lease.
		// Re-acquisition by the current holder succeeds and moves the expiry.
		Task<bool> TryAcquireAsync( string schedule, string claimant, DateTime expiry );

		// Extends the schedule's lease to expiry iff claimant currently holds an unexpired
		// lease; returns false otherwise (the lease lapsed and may have been acquired by a
		// peer).
		Task<bool> RenewAsync( string schedule, string claimant, DateTime expiry );

		// Releases the schedule's lease iff held by claimant; a lapsed or foreign-held lease is
		// left untouched.
		Task ReleaseAsync( string schedule, string claimant );

		// Currently held lease on the schedule, or null when unheld or expired. For composition
		// into ScheduleInfo and diagnostics — never for claim decisions, which must go through
		// TryAcquireAsync/RenewAsync.
		Task<ExecutionLease> GetAsync( string schedule );
	}
}
