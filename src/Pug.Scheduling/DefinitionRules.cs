using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// How a definition update affects the schedule's state. A pure function of the schedule (as
	// locked for the update), the new definition and the time.
	internal static class DefinitionRules
	{
		// The schedule's next occurrence once its definition is replaced by definition at now. A
		// changed recurrence restarts occurrence computation from now, unless a retry is pending:
		// the retry keeps its time, and the new recurrence applies once the occurrence ends. An
		// unchanged recurrence keeps the next occurrence the schedule has now.
		public static DateTime? NextRunAfterUpdate( ScheduleInfo schedule, ScheduleDefinition definition, DateTime now )
		{
			bool recurrenceChanged = definition.Recurrence != schedule.Definition.Recurrence;

			return recurrenceChanged && schedule.State.Attempt == 0
						? RecurrenceCalculator.GetNextOccurrence( definition.Recurrence, now )
						: schedule.State.NextRun;
		}
	}
}
