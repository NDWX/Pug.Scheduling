using System;

namespace Pug.Scheduling.Abstractions
{
	// The range of durations the framework hands to timers (CancellationTokenSource, Task.Delay),
	// which reject anything longer than about 49.7 days. Defined once so that the scheduler
	// (rejecting at registration) and the worker (defending against a remote coordinator) agree.
	public static class TimerBounds
	{
		// The longest duration a timer accepts: 2^32 - 2 milliseconds.
		public static readonly TimeSpan Maximum = TimeSpan.FromMilliseconds( uint.MaxValue - 1 );

		// Whether the duration is not negative and no longer than Maximum.
		public static bool Contains( TimeSpan duration ) => duration >= TimeSpan.Zero && duration <= Maximum;
	}
}
