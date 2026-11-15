using Pug.Scheduling.Models;

namespace Pug.Scheduling.Data.Abstractions
{
	// The schedule's occurrence-progress columns, written together by IDataSession
	// .SetOccurrenceStateAsync: retry attempt, the retried occurrence, the next occurrence (or the
	// retry time), the last-run summary and the run-request marker. The values are decided by the
	// caller; the store writes them as given.
	public sealed record OccurrenceProgress( int Attempt,
											DateTime? Occurrence,
											DateTime? NextRun,
											RunSummary? LastRun,
											bool RunRequested );
}
