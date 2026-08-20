using Pug.Application.Data;
using Pug.Effable;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Data.Abstractions
{
	// Storage contract for the schedule store, consumed only by the core Scheduler facade and
	// ExecutionCoordinator implementation — never by workers (they go through
	// IExecutionCoordinator) and never by API consumers (they go through IScheduler). Methods are
	// storage primitives: policy (authorization, validation, due-ness rules, misfire/retry
	// decisions, next-occurrence computation) lives with the callers, which compose primitives
	// into atomic units using the inherited transaction control. Execution leases are not this
	// store's concern: they live behind IExecutionLeaseProvider, in their own storage.
	public interface IDataSession : IApplicationDataSession
	{
		// == Management operations (Scheduler facade) ==

		Task InsertAsync( ScheduleInfo schedule );

		Task<ScheduleInfo?> GetScheduleAsync( string identifier );

		// Lists schedules within a domain, optionally filtered by owner principal and/or job
		// type. Never crosses domains.
		Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference? owner = null, string? jobType = null );

		// Replaces the schedule's definition columns, stores the recomputed next occurrence, and
		// stamps the row's lastUpdate column (reserved for definition updates).
		Task UpdateDefinitionAsync( string schedule, ScheduleDefinition definition, DateTime? nextRun, ActionContext<Reference> lastUpdate );

		// Deletes the schedule and (by cascade) its run history.
		Task DeleteAsync( string schedule );

		Task SetPausedAsync( string schedule, bool paused );

		// Moves the schedule's next occurrence without touching the rest of its state; clears any
		// pending run request.
		Task UpdateNextRunAsync( string schedule, DateTime? nextRun );

		// Requests an immediate run: moves the next occurrence to requestedTime and raises the
		// RunRequested marker, which the coordinator consumes to classify the run as MANUAL.
		Task RequestRunAsync( string schedule, DateTime requestedTime );

		// Lowers the RunRequested marker once the requested run has been claimed.
		Task ClearRunRequestAsync( string schedule );

		// == Execution operations (ExecutionCoordinator) ==

		// Schedules that are enabled, not paused, and due (NextRun elapsed by dueBy), ordered by
		// NextRun. A plain read with no claim semantics: exclusive execution is arbitrated by
		// IExecutionLeaseProvider, so concurrent coordinators may read overlapping candidates
		// and race for the lease.
		Task<IEnumerable<ScheduleInfo>> GetDueSchedulesAsync( DateTime dueBy, int maximumSchedules );

		Task InsertRunAsync( ScheduleRun run );

		Task<ScheduleRun?> GetRunAsync( string identifier );

		// The schedule's most recently started run, or null when it has never run. Used by the
		// coordinator to carry the original occurrence time into retries, and to adopt the
		// unfinished run of a lapsed claim instead of inserting a duplicate.
		Task<ScheduleRun?> GetLatestRunAsync( string schedule );

		// Records the run's terminal state (finished timestamp, worker classification, handler
		// outcome).
		Task CompleteRunAsync( string run, DateTime finished, string result, JobOutcome? outcome );

		// Records occurrence progression on the schedule: retry attempt counter, next occurrence,
		// and last-run summary in one update; clears any pending run request. Callers must hold
		// the schedule's execution lease — this is not verified here.
		Task RecordOccurrenceCompletionAsync( string schedule, int attempt, DateTime? nextRun, RunSummary lastRun );

		// Recorded runs of the schedule, most recent first, optionally bounded (inclusive) by
		// start timestamp.
		Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule, DateTime? from = null, DateTime? to = null );
	}
}
