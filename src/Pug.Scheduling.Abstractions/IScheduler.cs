using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Pug.Effable;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Management facade over the schedule store: configuration and observation only. This
	// interface never executes jobs — run-now merely records a request for a worker to
	// claim. It is the single wire contract implemented by the in-process facade (and any future
	// HTTP client), so keep it the source of truth for the scheduler's capabilities.
	//
	// Authorization: every operation is checked against the schedule's domain. The schedule's
	// Owner principal may always create their own schedules and view, modify, and delete
	// schedules they own; other principals require the corresponding role-based ACL grant.
	public interface IScheduler
	{
		// Registers a new schedule and returns its identifier. owner is the principal on whose
		// behalf the job executes, defaulting to the current security principal when omitted;
		// registering a schedule for oneself requires no ACL grant, while registering on behalf
		// of another principal requires the CREATE grant. The definition is validated
		// (recurrence well-formed, known time zone, registered job type, payload accepted by the
		// job type's IJobPayloadValidator when registered); omitted policies and timeout are
		// assigned service-level defaults. The schedule starts enabled with its first occurrence
		// computed from the recurrence.
		Task<string> RegisterAsync( ScheduleDefinition definition, Reference owner = null );

		// Lists schedules within a domain, optionally filtered by owner principal and/or job
		// type. Results are always domain-scoped; there is no cross-domain listing.
		Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference owner = null, string jobType = null );

		// Throws UnknownScheduleException when the schedule does not exist.
		Task<ScheduleInfo> GetScheduleAsync( string identifier );

		// Replaces the schedule's definition wholesale, re-validating as for registration, and
		// recomputes the next occurrence from now when the recurrence changed and the schedule is
		// idle (no run in progress, no retry pending). During a run, or with a retry pending,
		// the next occurrence is left alone: the run's completion (or the retry's occurrence
		// ending) then applies the new recurrence from the occurrence it ran.
		// A run in progress, a pending retry and a run request are themselves left as they are.
		// Domain is immutable: a definition carrying a different value is rejected (the
		// schedule's Owner is not part of the definition and can never change). Updates the
		// schedule's LastUpdate. Throws UnknownScheduleException when the schedule does not exist.
		Task UpdateDefinitionAsync( string identifier, ScheduleDefinition definition );

		// Deletes the schedule and its run history. A run currently executing is not interrupted,
		// but its completion is recorded nowhere. Throws UnknownScheduleException when the
		// schedule does not exist.
		Task DeleteAsync( string identifier );

		// Suspends firing without losing the schedule: occurrences elapsing while paused are
		// subject to the misfire policy upon resume. A run already claimed continues to
		// completion. Throws UnknownScheduleException when the schedule does not exist.
		Task PauseAsync( string identifier );

		// Lifts a pause and recomputes the next occurrence, applying the misfire policy to
		// occurrences elapsed while paused. Throws UnknownScheduleException when the schedule
		// does not exist.
		Task ResumeAsync( string identifier );

		// Requests an immediate run: a worker claims and executes the schedule on its next poll,
		// with trigger MANUAL, whatever its next occurrence. The request never moves the next
		// occurrence. A manual run is not retried on failure; during a retry's backoff it
		// accelerates the pending retry instead (same occurrence, same attempt). On completion,
		// FIRE_ONCE and SKIP schedules coalesce an occurrence that was already due into it, while
		// RUN_ALL keeps its backlog. Throws RunInProgressException when a run of the schedule is
		// in progress, including a lapsed run awaiting adoption, and UnknownScheduleException
		// when the schedule does not exist.
		Task RunNowAsync( string identifier );

		// Returns the schedule's recorded runs, most recent first, optionally bounded by start
		// timestamp (inclusive). Throws UnknownScheduleException when the schedule does not exist.
		Task<IEnumerable<ScheduleRun>> GetRunsAsync( string identifier, DateTime? from = null, DateTime? to = null );
	}
}
