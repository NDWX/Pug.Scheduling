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
	//
	// Every call identifies its caller by claimant and worker. claimant is the worker group's
	// identity — the one grants are qualified by — and must not contain '/'. worker is the
	// worker's number within that group (1 or greater). Claims are held per worker
	// ("claimant/worker"), so two workers of one group never share a claim, while a restarted
	// worker presenting the same claimant and number re-takes the claims its predecessor held.
	public interface IExecutionCoordinator
	{
		// Atomically claims one due occurrence for the calling worker and returns it with its
		// execution parameters, or null when nothing is due. jobType restricts the claim to that
		// job type; null claims any type. A claimed occurrence is excluded from other workers
		// until its lease expires or its completion is reported.
		Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType );

		// Extends the lease of a claimed run during long executions. Returns false when the
		// claim is no longer held by the worker (it expired and may have been reclaimed by a
		// peer); the worker must then abandon the run without reporting completion.
		Task<bool> RenewClaimAsync( string run, string claimant, int worker );

		// Reports the terminal state of a claimed run: records run history, releases the claim,
		// applies the schedule's RetryPolicy when the outcome is unsuccessful, and computes the
		// schedule's next occurrence. result is a well-known RunResults value — the worker's
		// classification of the run; outcome is the job handler's report, null for runs that
		// never reached the handler (e.g. SKIPPED).
		Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome );
	}
}
