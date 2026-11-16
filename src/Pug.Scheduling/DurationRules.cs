namespace Pug.Scheduling
{
	// The rule shared by every schedule duration (interval, misfire threshold, retry backoff,
	// timeout and their defaults): whole seconds. The stores keep durations as whole seconds, so a
	// fraction is refused up front rather than silently truncated (to zero, for a sub-second value).
	internal static class DurationRules
	{
		// The least execution timeout a schedule may specify, and the least DefaultTimeout. A
		// shorter timeout leaves a handler too little time to do anything useful once worker
		// overheads (handler creation, the claim's round trips, cancellation) are counted, so it
		// is refused rather than producing runs that time out by construction.
		public static readonly TimeSpan MinimumTimeout = TimeSpan.FromSeconds( 5 );

		public static void RequireWholeSeconds( TimeSpan duration, string what, string parameter )
		{
			if( duration.Ticks % TimeSpan.TicksPerSecond != 0 )
				throw new ArgumentException( $"{what} must be a whole number of seconds.", parameter );
		}
	}
}
