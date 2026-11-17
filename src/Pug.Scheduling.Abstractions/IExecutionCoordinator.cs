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
	//
	// SECURITY: the scheduler's implementation checks CoordinatorSecurityOperations only when
	// ISecurityManager reports a principal; a call without one is trusted as in-process (D16).
	// MapExecutionCoordinatorEndpoints never lets a call through without a principal. Any other
	// transport bound to this interface on the scheduler side — a host's own controller, a gRPC
	// service, a message consumer — must establish the caller's principal before calling it, or
	// every coordinator grant is skipped for its callers.
	//
	// FAILURES, as a worker must read them (D29). Three kinds, and only the first is about the caller:
	// - Refused: NotAuthorized (Pug.Application.Security; 403 over HTTP). The caller's claimant lacks
	//   the operation's CoordinatorSecurityOperations grant — or the security provider is failing
	//   closed while its backend is down, which reads the same. It concerns the caller, not the run.
	//   SchedulerWorker therefore treats a refused renewal or report as unconfirmed (retried within
	//   the lease), and a refused claim as a reason to poll more slowly; it never pauses itself.
	// - Not held: RenewClaimAsync/AbandonClaimAsync answering false, ClaimNotHeldException,
	//   UnknownRunException. The claim is gone (lapsed and adopted, finished, deleted); normal at run
	//   level, and nothing the caller does will bring it back.
	// - Unconfirmed: anything else — AuthenticationException (401: a token may refresh), a server or
	//   transport failure, a timeout, ArgumentException or InvalidOperationException (one schedule's
	//   bad stored data must not be read as the caller's fault). The call may or may not have taken
	//   effect; the claim stands until its lease lapses.
	//
	// TIMEOUTS. A worker bounds its renewals by its claim's deadline, but not a single completion
	// report: an implementation must keep every call finite — an HTTP transport by its client's
	// timeout (HttpClient's default is 100 s), a database by its command and lock timeouts (Npgsql's
	// command timeout, 30 s by default; SQLite's busy timeout). A hung call holds its worker, and
	// a host's shutdown budget must allow for one report attempt in flight.
	public interface IExecutionCoordinator
	{
		// Atomically claims one due occurrence for the calling worker and returns it with its
		// execution parameters, or null when nothing is due. jobType restricts the claim to that
		// job type; null claims any type. A claimed occurrence is excluded from other workers
		// until its lease expires or its completion is reported. The claim stands for
		// ClaimedJob.ClaimDuration from the call unless renewed; the caller's own unfinished run is
		// handed back to it (adopted) even while its lease is live, so a worker must not poll again
		// while it still holds a claim it means to keep or report.
		Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType );

		// Extends the lease of a claimed run during long executions, to ClaimedJob.ClaimDuration
		// from the call. Returns false when the claim is no longer held by the worker (it expired
		// and may have been reclaimed by a peer); the worker must then abandon the run without
		// reporting completion.
		Task<bool> RenewClaimAsync( string run, string claimant, int worker );

		// Releases a claim the worker is giving up on a graceful shutdown, so a peer can adopt the
		// run at its next claim without waiting for the lease to lapse: the lease is expired, while
		// the run and its holder are kept (the next claim classifies it as an adoption). Returns
		// true when the caller held the claim and it was released; false when it is not held (it
		// was adopted by a peer, the run finished, the run is unknown, or the schedule was
		// deleted). Never throws ClaimNotHeldException: abandoning is best effort.
		Task<bool> AbandonClaimAsync( string run, string claimant, int worker );

		// Reports the terminal state of a claimed run: records run history, releases the claim,
		// applies the schedule's RetryPolicy when the outcome is unsuccessful, and computes the
		// schedule's next occurrence. result is a well-known RunResults value — the worker's
		// classification of the run; outcome is the job handler's report, null for runs that
		// never reached the handler (e.g. SKIPPED). Throws ClaimNotHeldException, writing nothing,
		// when the reporter cannot be verified to hold the claim on the run (it was adopted by
		// another worker, never claimed by this one, or has already finished).
		Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome );
	}
}
