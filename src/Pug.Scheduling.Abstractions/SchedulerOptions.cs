using System;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Abstractions
{
	// Service-level defaults applied to registrations that omit optional policies.
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

		public TimeSpan DefaultTimeout { get; init; } = TimeSpan.FromMinutes( 10 );
	}
}
