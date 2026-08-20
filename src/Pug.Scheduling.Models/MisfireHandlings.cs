namespace Pug.Scheduling.Models
{
	// Well-known values of MisfirePolicy.Handling.
	public static class MisfireHandlings
	{
		// Coalesce all missed occurrences into one immediate run, then resume the normal
		// recurrence. Sensible default for reports: the recipient gets one catch-up copy.
		public const string FireOnce = "FIRE_ONCE";

		// Skip missed occurrences entirely and advance to the next future occurrence.
		public const string Skip = "SKIP";

		// Fire every missed occurrence individually, in order. Rarely wanted; only for jobs where
		// each occurrence has distinct meaning (e.g. period-covering exports).
		public const string RunAll = "RUN_ALL";
	}
}
