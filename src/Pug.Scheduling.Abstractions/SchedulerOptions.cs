using System;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Service-level defaults applied to registrations that omit optional policies, and the
	// host-level limits on schedule durations. Every duration is whole seconds.
	public record SchedulerOptions
	{
		public MisfirePolicy DefaultMisfirePolicy { get; init; } = new()
		{
			Handling = MisfireHandlings.FireOnce,
			Threshold = TimeSpan.FromMinutes( 5 )
		};

		public RetryPolicy DefaultRetryPolicy { get; init; } = new()
		{
			MaximumAttempts = 3,
			Backoff = TimeSpan.FromMinutes( 1 )
		};

		// The shortest misfire threshold a schedule (or DefaultMisfirePolicy) may have. A threshold
		// smaller than the time a claim takes to arrive would turn every claim into a misfire, and
		// under SKIP skip nearly every occurrence. Whole seconds, not negative.
		public TimeSpan MinimumMisfireThreshold { get; init; } = TimeSpan.FromSeconds( 15 );

		public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes( 10 );
	}
}
