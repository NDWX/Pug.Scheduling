using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// How a definition update affects the schedule's state. A pure function of the schedule (as
	// locked for the update), the new definition and the time.
	internal static class DefinitionRules
	{
		// The schedule's next occurrence once its definition is replaced by definition at now. A
		// changed recurrence restarts occurrence computation from now only while the schedule is
		// idle: no run in progress (an execution lease is held) and no retry pending. During a run
		// nextRun is the active occurrence and is left alone, and the run's completion applies the
		// new recurrence from it; a pending retry keeps its time likewise. An unchanged recurrence
		// keeps the next occurrence the schedule has now.
		public static DateTime? NextRunAfterUpdate( ScheduleInfo schedule, ScheduleDefinition definition, DateTime now )
		{
			bool recurrenceChanged = definition.Recurrence != schedule.Definition.Recurrence;

			bool idle = schedule.State.Attempt == 0 && schedule.ExecutionLease is null;

			return recurrenceChanged && idle
						? RecurrenceCalculator.GetFirstOccurrence( definition.Recurrence, now )
						: schedule.State.NextRun;
		}
	}
}
