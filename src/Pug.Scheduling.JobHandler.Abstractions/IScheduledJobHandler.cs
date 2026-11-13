using System;
using System.Threading;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.JobHandler.Abstractions
{
	// Executes the business logic of one run of a job type. A handler is created per run by the
	// IScheduledJobHandlerFactory registered for the job type, and disposed by the worker after
	// the run: a handler that needs cleanup may implement IAsyncDisposable or IDisposable.
	public interface IScheduledJobHandler
	{
		// Executes one occurrence of a schedule and returns its outcome: Successful with optional
		// Details, or unsuccessful with Error. Implementations are third-party code from the
		// scheduler's perspective, and the worker is defensive accordingly: any exception
		// escaping the handler is caught and recorded as an unsuccessful outcome carrying the
		// exception in Error, and a null return is treated as unsuccessful. Unsuccessful
		// occurrences are retried per the schedule's RetryPolicy.
		// timeout is the schedule's execution timeout, which the worker enforces.
		// cancellationToken fires when that timeout elapses, on host shutdown, or when the claim
		// is lost; honor it promptly — the worker does not rely on it being honored (lease
		// expiry bounds a runaway handler), but a handler that overruns loses its
		// at-most-once-per-lease guarantee and risks concurrent re-execution by a peer.
		//
		// Execution is at-least-once: a crashed worker's occurrence is re-executed by a peer once
		// the lease expires. Key externally visible side effects (emails, downstream writes) on
		// (context.Schedule, context.ScheduledFor) so a re-executed or retried occurrence is not
		// double-applied.
		Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout, CancellationToken cancellationToken );
	}
}
