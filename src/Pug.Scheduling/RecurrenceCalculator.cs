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

		// The first occurrence of a recurrence registered (or changed) at nowUtc, or null when it is
		// already exhausted (past NotAfter).
		//
		// - Interval with NotBefore: NotBefore anchors the grid, so this is the smallest
		//   NotBefore + k*Interval (k >= 0) that is not before now.
		// - Interval without NotBefore: now + Interval.
		// - Cron: the first occurrence after now, or at NotBefore itself when that is still ahead.
		public static DateTime? GetFirstOccurrence( Recurrence recurrence, DateTime nowUtc )
		{
			if( recurrence == null )
				throw new ArgumentNullException( nameof(recurrence) );

			nowUtc = DateTime.SpecifyKind( nowUtc, DateTimeKind.Utc );

			if( recurrence.Interval is not null && recurrence.NotBefore is not null )
				return NotAfterClamped(
						recurrence,
						IntervalGridPoint( AsUtc( recurrence.NotBefore.Value ), recurrence.Interval.Value, nowUtc, true ) );

			return GetOccurrenceAfter( recurrence, nowUtc, nowUtc );
		}

		// The occurrence that follows previousOccurrence once time has reached afterUtc: the first
		// one strictly after both. An interval recurrence keeps the grid previousOccurrence is on
		// (previous + k*Interval, smallest k >= 1 landing after afterUtc), so late completions
		// never shift its phase and missed slots coalesce onto the grid. Null when exhausted
		// (past NotAfter).
		public static DateTime? GetNextOccurrence( Recurrence recurrence, DateTime previousOccurrence, DateTime afterUtc )
		{
			if( recurrence == null )
				throw new ArgumentNullException( nameof(recurrence) );

			previousOccurrence = AsUtc( previousOccurrence );
			afterUtc = AsUtc( afterUtc );

			return GetOccurrenceAfter(
					recurrence, previousOccurrence, afterUtc > previousOccurrence ? afterUtc : previousOccurrence );
		}

		// The first occurrence strictly after after, on the grid anchored at anchor (interval) and
		// not before NotBefore (inclusive of it), or null past NotAfter.
		private static DateTime? GetOccurrenceAfter( Recurrence recurrence, DateTime anchor, DateTime after )
		{
			bool atNotBefore = recurrence.NotBefore is not null && AsUtc( recurrence.NotBefore.Value ) > after;
			DateTime from = atNotBefore ? AsUtc( recurrence.NotBefore!.Value ) : after;

			DateTime? occurrence = recurrence.Interval is not null
										? IntervalGridPoint( anchor, recurrence.Interval.Value, from, atNotBefore )
										: CronOccurrence( recurrence, from, atNotBefore );

			return NotAfterClamped( recurrence, occurrence );
		}

		// The smallest anchor + k*interval (k >= 0) after from, or at from when inclusive: O(1).
		private static DateTime IntervalGridPoint( DateTime anchor, TimeSpan interval, DateTime from, bool inclusive )
		{
			long elapsed = ( from - anchor ).Ticks;

			if( elapsed < 0 )
				return anchor;

			long steps = elapsed / interval.Ticks;

			if( !inclusive || elapsed % interval.Ticks != 0 )
				steps++;

			return anchor.AddTicks( steps * interval.Ticks );
		}

		private static DateTime? CronOccurrence( Recurrence recurrence, DateTime from, bool inclusive )
		{
			TimeZoneInfo timeZone = TimeZoneInfo.FindSystemTimeZoneById( recurrence.TimeZone );

			return CronExpression.Parse( recurrence.CronExpression ).GetNextOccurrence( from, timeZone, inclusive );
		}

		private static DateTime? NotAfterClamped( Recurrence recurrence, DateTime? occurrence ) =>
			occurrence is not null && recurrence.NotAfter is not null && occurrence > recurrence.NotAfter
				? null
				: occurrence;

		private static DateTime AsUtc( DateTime value ) => DateTime.SpecifyKind( value, DateTimeKind.Utc );
	}
}
