using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// What a claim does with a schedule, as decided by OccurrenceState.Classify.
	public enum OccurrenceAction
	{
		// Nothing to do: not due, paused or disabled, or its run is held by a live peer.
		NotDue,

		// Take over the active run: its lease lapsed (a crashed worker), or the caller already
		// holds it (a restarted worker presenting the same holder).
		Adopt,

		// Start a run requested by run-now.
		Manual,

		// Start the pending retry of the occurrence being retried.
		Retry,

		// Start the occurrence, overdue beyond the misfire threshold (FIRE_ONCE, RUN_ALL).
		Misfire,

		// Start the occurrence on time.
		Schedule,

		// Dispose of the occurrence, overdue beyond the misfire threshold, without running it
		// (SKIP).
		Skip
	}

	// Classifies a schedule for a claim, and derives the run a claim starts. Pure functions of the
	// schedule's state: the coordinator applies them to the schedule row it has locked, and the
	// store's due-candidate pre-filter (IDataSession.GetDueCandidatesAsync) mirrors Classify.
	public static class OccurrenceState
	{
		// Precedence: adopt (a run in progress always comes first, so its occurrence is never
		// recorded twice) > MANUAL > RETRY > MISFIRE / SKIP / SCHEDULE. holder is the claiming
		// worker, "{claimant}/{worker}".
		public static OccurrenceAction Classify( ScheduleInfo schedule, DateTime now, string holder )
		{
			ScheduleState state = schedule.State;

			if( !state.Enabled || state.Paused )
				return OccurrenceAction.NotDue;

			ExecutionLease lease = schedule.ExecutionLease;

			if( lease is not null )
				return lease.ClaimedBy == holder || lease.Expiry <= now
							? OccurrenceAction.Adopt
							: OccurrenceAction.NotDue;

			if( state.RunRequested )
				return OccurrenceAction.Manual;

			if( state.NextRun is null || state.NextRun > now )
				return OccurrenceAction.NotDue;

			if( state.Attempt > 0 )
				return OccurrenceAction.Retry;

			if( now - state.NextRun.Value <= schedule.Definition.MisfirePolicy.Threshold )
				return OccurrenceAction.Schedule;

			return schedule.Definition.MisfirePolicy.Handling == MisfireHandlings.Skip
						? OccurrenceAction.Skip
						: OccurrenceAction.Misfire;
		}

		// The occurrence a run started for action is recorded under: the idempotency key
		// (Schedule, ScheduledFor) handlers see. A retry, and a manual run during a retry's
		// backoff (which accelerates it), keep the original occurrence; any other manual run is
		// for the time it was claimed; a scheduled or misfired run is for its next occurrence.
		public static DateTime ScheduledFor( ScheduleInfo schedule, OccurrenceAction action, DateTime now )
		{
			ScheduleState state = schedule.State;

			return action switch
			{
				OccurrenceAction.Manual when state.Attempt > 0 => RetriedOccurrence( state ),
				OccurrenceAction.Manual => now,
				OccurrenceAction.Retry => RetriedOccurrence( state ),
				OccurrenceAction.Misfire or OccurrenceAction.Schedule or OccurrenceAction.Skip =>
					DateTime.SpecifyKind( state.NextRun!.Value, DateTimeKind.Utc ),
				_ => throw new ArgumentOutOfRangeException( nameof(action), action, "No run is started for this action." )
			};
		}

		// A retry carries its occurrence (schema v3); a retry recorded before then, with no
		// occurrence to carry, can only be keyed by its retry time.
		private static DateTime RetriedOccurrence( ScheduleState state ) =>
			DateTime.SpecifyKind( state.Occurrence ?? state.NextRun!.Value, DateTimeKind.Utc );

		public static string Trigger( OccurrenceAction action ) =>
			action switch
			{
				OccurrenceAction.Manual => RunTriggers.Manual,
				OccurrenceAction.Retry => RunTriggers.Retry,
				OccurrenceAction.Misfire or OccurrenceAction.Skip => RunTriggers.Misfire,
				OccurrenceAction.Schedule => RunTriggers.Schedule,
				_ => throw new ArgumentOutOfRangeException( nameof(action), action, "No run is started for this action." )
			};
	}
}
