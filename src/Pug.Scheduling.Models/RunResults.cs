namespace Pug.Scheduling.Models
{
	// Well-known values of ScheduleRun.Result and RunSummary.Result.
	public static class RunResults
	{
		public const string Succeeded = "SUCCEEDED";

		public const string Failed = "FAILED";

		// The run's timeout elapsed before the handler completed.
		public const string TimedOut = "TIMED_OUT";

		// The run was declined without executing (e.g. misfire SKIP handling or a tenant quota),
		// with the reason recorded in Detail.
		public const string Skipped = "SKIPPED";
	}
}
