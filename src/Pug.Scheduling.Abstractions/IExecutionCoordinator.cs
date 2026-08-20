using System.Collections.Generic;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Execution-side facade between workers and the scheduler: workers poll and claim due
	// occurrences, heartbeat their claims, and report outcomes exclusively through this surface —
	// never against the data backend directly, regardless of where the worker is hosted. What
	// makes an occurrence 'due', claim/lease semantics, misfire handling, retry scheduling, and
	// next-occurrence computation are all implemented behind this facade; workers know nothing of
	// them. An in-process worker binds this interface to the scheduler's own implementation; an
	// externally hosted worker binds it to a transport client (e.g. HTTP) of the same contract.
	public interface IExecutionCoordinator
	{
		// Atomically claims up to maximumJobs due occurrences for the calling worker instance and
		// returns them with their execution parameters. A claimed occurrence is excluded from
		// other claimants until its lease expires or its completion is reported. Returns an empty
		// result when nothing is due.
		Task<IEnumerable<ClaimedJob>> ClaimDueJobsAsync( string claimant, int maximumJobs );

		// Extends the lease of a claimed run during long executions. Returns false when the
		// claim is no longer held by claimant (it expired and may have been reclaimed by a peer);
		// the worker must then abandon the run without reporting completion.
		Task<bool> RenewClaimAsync( string run, string claimant );

		// Reports the terminal state of a claimed run: records run history, releases the claim,
		// applies the schedule's RetryPolicy when the outcome is unsuccessful, and computes the
		// schedule's next occurrence. result is a well-known RunResults value — the worker's
		// classification of the run; outcome is the job handler's report, null for runs that
		// never reached the handler (e.g. SKIPPED).
		Task ReportCompletionAsync( string run, string claimant, string result, JobOutcome outcome );
	}
}
