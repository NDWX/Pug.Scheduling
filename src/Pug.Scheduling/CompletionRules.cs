using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// How a schedule's occurrence progresses when a run of it completes or an occurrence is
	// skipped. Pure functions of the schedule (as locked for the transition), the run and the
	// time: the coordinator writes what they return.
	internal static class CompletionRules
	{
		// The schedule's progress once run, executed for it, finished with result at now.
		//
		// - A manual run (run-now, attempt 0) is outside the occurrence grid and is never retried.
		//   It leaves the next occurrence alone, except that FIRE_ONCE and SKIP coalesce an
		//   occurrence already due when the run started into it; RUN_ALL keeps its backlog.
		// - Success, or a failure with no attempt left, ends the occurrence: the attempt resets and
		//   the schedule advances (NextOccurrence).
		// - Any other failure schedules a retry of the same occurrence with exponential backoff,
		//   Backoff * 2^(retry - 1) from now. A manual run during a retry's backoff carries that
		//   retry's attempt, so it counts as the retry.
		//
		// The run request is carried over as it stands: these rules never consume one.
		//
		// finished is when the run actually ended, recorded as the schedule's last run; it defaults
		// to now, and differs only when a completion is applied after the fact (a repair).
		public static OccurrenceProgress Next( ScheduleInfo schedule, ScheduleRun run, string result, DateTime now,
												DateTime? finished = null )
		{
			ScheduleDefinition definition = schedule.Definition;
			ScheduleState state = schedule.State;
			RunSummary lastRun = new() { Started = run.Started, Finished = finished ?? now, Result = result };

			if( IsManual( run ) )
				return new OccurrenceProgress(
						state.Attempt, state.Occurrence, NextRunAfterManualRun( schedule, run, now ), lastRun,
						state.RunRequested );

			int nextAttempt = run.Attempt + 1;

			if( result != RunResults.Succeeded && nextAttempt < definition.RetryPolicy.MaximumAttempts )
				return new OccurrenceProgress(
						nextAttempt,
						DateTime.SpecifyKind( run.ScheduledFor, DateTimeKind.Utc ),
						now + definition.RetryPolicy.Backoff * Math.Pow( 2, nextAttempt - 1 ),
						lastRun,
						state.RunRequested );

			return new OccurrenceProgress(
					0, null, NextOccurrence( definition, run.ScheduledFor, now ), lastRun, state.RunRequested );
		}

		// The schedule's progress once its due occurrence is skipped (misfire SKIP) at now.
		public static OccurrenceProgress Skipped( ScheduleInfo schedule, DateTime occurrence, DateTime now ) =>
			new(
					0,
					null,
					NextOccurrence( schedule.Definition, occurrence, now ),
					new RunSummary { Started = now, Finished = now, Result = RunResults.Skipped },
					schedule.State.RunRequested );

		// The occurrence after occurrence, which ended (completed, terminally failed, skipped or
		// coalesced) at now. RUN_ALL executes every missed occurrence, so it steps once from the
		// one that ended; the other policies coalesce every slot missed by now onto the recurrence's
		// grid, keeping its phase.
		public static DateTime? NextOccurrence( ScheduleDefinition definition, DateTime occurrence, DateTime now )
		{
			DateTime after = definition.MisfirePolicy.Handling == MisfireHandlings.RunAll ? occurrence : now;

			return RecurrenceCalculator.GetNextOccurrence( definition.Recurrence, occurrence, after );
		}

		// A manual run proper: requested by run-now with no retry pending. One claimed during a
		// retry's backoff carries the retry's attempt and is the retry.
		private static bool IsManual( ScheduleRun run ) => run.Trigger == RunTriggers.Manual && run.Attempt == 0;

		private static DateTime? NextRunAfterManualRun( ScheduleInfo schedule, ScheduleRun run, DateTime now )
		{
			DateTime? nextRun = schedule.State.NextRun;

			bool coalesce = schedule.Definition.MisfirePolicy.Handling != MisfireHandlings.RunAll &&
							nextRun is not null && nextRun <= run.Started;

			return coalesce ? NextOccurrence( schedule.Definition, nextRun.Value, now ) : nextRun;
		}
	}
}
