using System.Threading;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Executes the business logic of one job type. Implementations are registered against their
	// job type key (AddScheduledJobHandler<THandler>( jobType )) and resolved per run from a
	// dedicated dependency-injection scope, so they may take scoped dependencies.
	public interface IScheduledJobHandler
	{
		// Executes one occurrence of a schedule and returns its outcome: Successful with optional
		// Details, or unsuccessful with Error. Implementations are third-party code from the
		// scheduler's perspective, and the worker is defensive accordingly: any exception
		// escaping the handler is caught and recorded as an unsuccessful outcome carrying the
		// exception in Error, and a null return is treated as unsuccessful. Unsuccessful
		// occurrences are retried per the schedule's RetryPolicy.
		// cancellationToken combines host shutdown with the schedule's execution timeout; honor
		// it promptly — the worker does not rely on it being honored (lease expiry bounds a
		// runaway handler), but a handler that overruns loses its at-most-once-per-lease
		// guarantee and risks concurrent re-execution by a peer.
		//
		// Execution is at-least-once: a crashed worker's occurrence is re-executed by a peer once
		// the lease expires. Key externally visible side effects (emails, downstream writes) on
		// (context.Schedule, context.ScheduledFor) so a re-executed or retried occurrence is not
		// double-applied.
		Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, CancellationToken cancellationToken );
	}
}
