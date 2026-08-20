using System.Threading;
using System.Threading.Tasks;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Transport strategy between the worker and job executors: one long-lived instance per worker
	// host, invoked once per claimed run. The default in-process dispatcher resolves the
	// IScheduledJobHandler registered for context.JobType and invokes it directly; alternative
	// implementations (e.g. a webhook dispatcher POSTing the context to a per-job-type endpoint)
	// swap in without changes to the worker loop, the schedule store, or handler contracts.
	public interface IScheduledJobDispatcher
	{
		// Delivers one occurrence to its executor and returns the outcome, with the same
		// defensive semantics as IScheduledJobHandler.ExecuteAsync: an exception escaping the
		// dispatcher, or a null return, is treated by the worker as an unsuccessful outcome.
		Task<JobOutcome> DispatchAsync( ScheduledJobContext context, CancellationToken cancellationToken );
	}
}
