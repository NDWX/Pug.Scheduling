namespace Pug.Scheduling.Models
{
	// Well-known values of ScheduleRun.Trigger and ScheduledJobContext.Trigger.
	public static class RunTriggers
	{
		// Fired by the schedule's own recurrence.
		public const string Schedule = "SCHEDULE";

		// Fired by an explicit run-now request.
		public const string Manual = "MANUAL";

		// Re-execution of a failed occurrence under the schedule's RetryPolicy.
		public const string Retry = "RETRY";

		// Catch-up execution of an occurrence missed while no worker was running, per the
		// schedule's MisfirePolicy.
		public const string Misfire = "MISFIRE";
	}
}
