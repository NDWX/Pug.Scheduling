using Pug.Application.Data;
using Pug.Effable;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Data.Abstractions
{
	// Storage contract for the schedule store, consumed only by the core Scheduler facade and
	// ExecutionCoordinator implementation — never by workers (they go through
	// IExecutionCoordinator) and never by API consumers (they go through IScheduler).
	//
	// Storage primitives only: sessions read, lock and write by identifier, and no member encodes
	// a rule. Due-ness, adoption, misfire, retry, manual-run rules, run-now refusal and when the
	// next occurrence may change are all decided by the callers, which compose primitives into
	// units of work (UnitsOfWork): every provider makes a unit one local transaction. A unit that
	// acts on what it read locks the row first (RowLock), decides, and writes the decided values.
	//
	// The execution lease lives on the schedule row (active run, holder, expiry) and is written
	// only by the execution coordinator's transitions.
	public interface IDataSession : IApplicationDataSession
	{
		// == Clock ==

		// The database's current time, UTC, read anew on every call: the actual time, not the start
		// of the unit's transaction, so a unit that waited on a row lock reads the time after the
		// wait. It is the one clock every coordinator host sharing the store agrees on. SQLite runs
		// in its host's process, so its clock is that host's clock: a SQLite store is single-host in
		// practice.
		Task<DateTime> GetCurrentTimeAsync();

		// == Schedules ==

		Task InsertAsync( ScheduleInfo schedule );

		// The schedule, or null when there is none - or, with RowLock.SkipLocked, when another unit
		// holds its row. rowLock other than None holds the row's lock until the unit of work ends,
		// so the unit can act on what it read (a write unit only). SQLite takes no row lock in any
		// mode: its write unit holds the database write lock from its first statement.
		Task<ScheduleInfo?> GetScheduleAsync( string identifier, RowLock rowLock = RowLock.None );

		// The schedule whose active run is the given run, or null when no schedule has it active
		// (the run finished, or it and its schedule were deleted). rowLock as for GetScheduleAsync.
		Task<ScheduleInfo?> GetScheduleByActiveRunAsync( string run, RowLock rowLock = RowLock.None );

		// Lists schedules within a domain, optionally filtered by owner principal and/or job
		// type. Never crosses domains.
		Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference? owner = null, string? jobType = null );

		// Identifiers of at most maximumSchedules enabled, unpaused schedules (of jobType, when not
		// null) that may have work for holder at now: idle with a run requested or an elapsed next
		// occurrence, or with an active run whose lease expired or is holder's own. Requested ones
		// first, then by next occurrence.
		//
		// A coarse pre-filter for index use only, mirroring OccurrenceState.Classify in
		// Pug.Scheduling: it may return candidates that no longer qualify by the time they are
		// locked, and the caller decides on the locked row.
		Task<IEnumerable<string>> GetDueCandidatesAsync( DateTime now, string holder, string? jobType, int maximumSchedules );

		// Replaces the schedule's definition columns, its next occurrence, and the row's
		// lastUpdate stamp (reserved for definition updates).
		Task UpdateDefinitionAsync( string schedule, ScheduleDefinition definition, DateTime? nextRun, ActionContext<Reference> lastUpdate );

		Task SetPausedAsync( string schedule, bool paused );

		Task SetRunRequestedAsync( string schedule, bool requested );

		// Sets the schedule's execution lease columns (active run, holder, expiry) to lease, or
		// clears them when lease is null.
		Task SetExecutionAsync( string schedule, ExecutionLease? lease );

		// Sets the schedule's occurrence-progress columns to progress.
		Task SetOccurrenceStateAsync( string schedule, OccurrenceProgress progress );

		// Deletes the schedule's run history, then the schedule.
		Task DeleteAsync( string schedule );

		// == Runs ==

		Task InsertRunAsync( ScheduleRun run );

		Task<ScheduleRun?> GetRunAsync( string identifier );

		// Recorded runs of the schedule, most recent first, optionally bounded (inclusive) by
		// start timestamp.
		Task<IEnumerable<ScheduleRun>> GetRunsAsync( string schedule, DateTime? from = null, DateTime? to = null );

		// Records the run's terminal state: finished timestamp, worker classification, handler
		// outcome.
		Task FinishRunAsync( string run, DateTime finished, string result, JobOutcome? outcome );

		// Records the worker that last claimed the run (ScheduleRun.ClaimedBy, an audit field).
		Task SetRunClaimantAsync( string run, string claimedBy );
	}
}
