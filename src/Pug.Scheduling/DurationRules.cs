namespace Pug.Scheduling
{
	// The rule shared by every schedule duration (interval, misfire threshold, retry backoff,
	// timeout and their defaults): whole seconds. The stores keep durations as whole seconds, so a
	// fraction is refused up front rather than silently truncated (to zero, for a sub-second value).
	internal static class DurationRules
	{
		public static void RequireWholeSeconds( TimeSpan duration, string what, string parameter )
		{
			if( duration.Ticks % TimeSpan.TicksPerSecond != 0 )
				throw new ArgumentException( $"{what} must be a whole number of seconds.", parameter );
		}
	}
}
