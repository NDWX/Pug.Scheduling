namespace Pug.Scheduling.Models
{
	// Well-known values of ScheduleRun.Result and RunSummary.Result.
	public static class RunResults
	{
		public const string Succeeded = "SUCCEEDED";

		public const string Failed = "FAILED";

		// The run's timeout elapsed before the handler completed.
		public const string TimedOut = "TIMED_OUT";

		// The run was declined without executing because of the misfire SKIP handling: the
		// occurrence was missed and passed over.
		public const string Skipped = "SKIPPED";
	}
}
