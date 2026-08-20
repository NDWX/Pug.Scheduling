using Cronos;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Next-occurrence computation and recurrence validation — the only place recurrence syntax
	// and time zone rules are interpreted. Cron occurrences are evaluated in the recurrence's
	// IANA time zone (Cronos handles DST: skipped local times jump forward, ambiguous times fire
	// once); interval occurrences are simple UTC arithmetic. All inputs and outputs are UTC.
	public static class RecurrenceCalculator
	{
		public static void Validate( Recurrence recurrence )
		{
			if( recurrence == null )
				throw new ArgumentNullException( nameof(recurrence) );

			bool hasCron = !string.IsNullOrWhiteSpace( recurrence.CronExpression );
			bool hasInterval = recurrence.Interval is not null;

			if( hasCron == hasInterval )
				throw new ArgumentException(
						"Recurrence must specify exactly one of CronExpression or Interval.",
						nameof(recurrence) );

			if( hasInterval && recurrence.Interval <= TimeSpan.Zero )
				throw new ArgumentException( "Recurrence interval must be positive.", nameof(recurrence) );

			if( string.IsNullOrWhiteSpace( recurrence.TimeZone ) )
				throw new ArgumentException( "Recurrence time zone is required.", nameof(recurrence) );

			try
			{
				TimeZoneInfo.FindSystemTimeZoneById( recurrence.TimeZone );
			}
			catch( Exception exception ) when( exception is TimeZoneNotFoundException or InvalidTimeZoneException )
			{
				throw new ArgumentException(
						$"Unknown or invalid time zone '{recurrence.TimeZone}'.", nameof(recurrence), exception );
			}

			if( hasCron )
			{
				try
				{
					CronExpression.Parse( recurrence.CronExpression );
				}
				catch( CronFormatException exception )
				{
					throw new ArgumentException(
							$"Invalid cron expression '{recurrence.CronExpression}'.", nameof(recurrence), exception );
				}
			}

			if( recurrence.NotBefore is not null && recurrence.NotAfter is not null &&
				recurrence.NotBefore >= recurrence.NotAfter )
				throw new ArgumentException( "NotBefore must precede NotAfter.", nameof(recurrence) );
		}

		// Next occurrence strictly after fromUtc (clamped to the recurrence's activation window),
		// or null when the recurrence is exhausted (past NotAfter).
		public static DateTime? GetNextOccurrence( Recurrence recurrence, DateTime fromUtc )
		{
			if( recurrence == null )
				throw new ArgumentNullException( nameof(recurrence) );

			fromUtc = DateTime.SpecifyKind( fromUtc, DateTimeKind.Utc );

			DateTime baseline = fromUtc;

			if( recurrence.NotBefore is not null && recurrence.NotBefore > baseline )
				baseline = DateTime.SpecifyKind( recurrence.NotBefore.Value, DateTimeKind.Utc );

			DateTime? next;

			if( recurrence.Interval is not null )
			{
				next = baseline + recurrence.Interval.Value;
			}
			else
			{
				TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById( recurrence.TimeZone );

				next = CronExpression.Parse( recurrence.CronExpression )
									.GetNextOccurrence( baseline, timeZone );
			}

			if( next is not null && recurrence.NotAfter is not null && next > recurrence.NotAfter )
				return null;

			return next;
		}
	}
}
