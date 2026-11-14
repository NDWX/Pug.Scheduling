using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests;

public class RecurrenceCalculatorTests
{
	private static DateTime Utc( int year, int month, int day, int hour = 0, int minute = 0 ) =>
		new( year, month, day, hour, minute, 0, DateTimeKind.Utc );

	private static Recurrence Hourly( DateTime? notBefore = null, DateTime? notAfter = null ) =>
		new() { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC", NotBefore = notBefore, NotAfter = notAfter };

	// == First occurrence ==

	[Fact]
	public void IntervalWithoutNotBeforeFirstOccursOneIntervalFromNow()
	{
		Assert.Equal( Utc( 2030, 1, 1, 1 ), RecurrenceCalculator.GetFirstOccurrence( Hourly(), Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void IntervalFirstOccursAtAFutureNotBefore()
	{
		Assert.Equal(
				Utc( 2030, 2, 1 ),
				RecurrenceCalculator.GetFirstOccurrence( Hourly( Utc( 2030, 2, 1 ) ), Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void IntervalFirstOccursOnTheGridAnchoredAtAPastNotBefore()
	{
		Recurrence recurrence = Hourly( Utc( 2030, 1, 1, 0, 10 ) );

		Assert.Equal( Utc( 2030, 1, 1, 3, 10 ), RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 1, 1, 2, 30 ) ) );
		Assert.Equal( Utc( 2030, 1, 1, 2, 10 ), RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 1, 1, 2, 10 ) ) );
	}

	[Fact]
	public void CronFirstOccursAtAFutureNotBeforeInclusive()
	{
		Recurrence recurrence = new()
		{
			CronExpression = "0 * * * *", TimeZone = "Etc/UTC", NotBefore = Utc( 2030, 2, 1 )
		};

		Assert.Equal( Utc( 2030, 2, 1 ), RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void CronFirstOccursStrictlyAfterNowOnceNotBeforeHasPassed()
	{
		Recurrence recurrence = new()
		{
			CronExpression = "0 * * * *", TimeZone = "Etc/UTC", NotBefore = Utc( 2029, 1, 1 )
		};

		Assert.Equal( Utc( 2030, 1, 1, 1 ), RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void FirstOccurrenceIsNullPastNotAfter()
	{
		Assert.Null( RecurrenceCalculator.GetFirstOccurrence(
				Hourly( notAfter: Utc( 2030, 1, 1, 0, 30 ) ), Utc( 2030, 1, 1 ) ) );
	}

	// == Next occurrence ==

	[Fact]
	public void IntervalNextKeepsTheGridPhaseAfterALateCompletion()
	{
		// Occurrence 00:00 completed at 00:47: the next one is 01:00, not 01:47.
		Assert.Equal(
				Utc( 2030, 1, 1, 1 ),
				RecurrenceCalculator.GetNextOccurrence( Hourly(), Utc( 2030, 1, 1 ), Utc( 2030, 1, 1, 0, 47 ) ) );
	}

	[Fact]
	public void IntervalNextCoalescesMissedSlotsOntoTheGrid()
	{
		// Slots 01:00..03:00 were missed by 03:20; the next is the first one after it.
		Assert.Equal(
				Utc( 2030, 1, 1, 4 ),
				RecurrenceCalculator.GetNextOccurrence( Hourly(), Utc( 2030, 1, 1 ), Utc( 2030, 1, 1, 3, 20 ) ) );
	}

	[Fact]
	public void IntervalNextIsStrictlyAfterWhenAfterIsOnTheGrid()
	{
		Assert.Equal(
				Utc( 2030, 1, 1, 4 ),
				RecurrenceCalculator.GetNextOccurrence( Hourly(), Utc( 2030, 1, 1 ), Utc( 2030, 1, 1, 3 ) ) );
	}

	[Fact]
	public void IntervalNextStepsOneSlotWhenAfterIsThePreviousOccurrence()
	{
		// RUN_ALL: after == previous walks the backlog one slot at a time.
		DateTime previous = Utc( 2030, 1, 1 );

		Assert.Equal( Utc( 2030, 1, 1, 1 ), RecurrenceCalculator.GetNextOccurrence( Hourly(), previous, previous ) );
		Assert.Equal(
				Utc( 2030, 1, 1, 2 ),
				RecurrenceCalculator.GetNextOccurrence( Hourly(), Utc( 2030, 1, 1, 1 ), Utc( 2030, 1, 1, 1 ) ) );
	}

	[Fact]
	public void IntervalNextHonorsAFutureNotBeforeOnTheGrid()
	{
		Recurrence recurrence = Hourly( Utc( 2030, 1, 1, 5, 30 ) );

		Assert.Equal(
				Utc( 2030, 1, 1, 6 ),
				RecurrenceCalculator.GetNextOccurrence( recurrence, Utc( 2030, 1, 1 ), Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void NextOccurrenceIsNullPastNotAfter()
	{
		Assert.Null( RecurrenceCalculator.GetNextOccurrence(
				Hourly( notAfter: Utc( 2030, 1, 1, 0, 30 ) ), Utc( 2030, 1, 1 ), Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void CronNextIsAfterTheLaterOfPreviousAndAfter()
	{
		Recurrence recurrence = new() { CronExpression = "0 * * * *", TimeZone = "Etc/UTC" };

		Assert.Equal(
				Utc( 2030, 1, 1, 4 ),
				RecurrenceCalculator.GetNextOccurrence( recurrence, Utc( 2030, 1, 1 ), Utc( 2030, 1, 1, 3, 20 ) ) );
		Assert.Equal(
				Utc( 2030, 1, 1, 1 ),
				RecurrenceCalculator.GetNextOccurrence( recurrence, Utc( 2030, 1, 1 ), Utc( 2030, 1, 1 ) ) );
	}

	[Fact]
	public void CronOccurrencesFollowTheRecurrenceTimeZone()
	{
		// Daily 02:00 Melbourne time; June = AEST (UTC+10), so 02:00 local = 16:00 UTC previous day.
		Recurrence recurrence = new() { CronExpression = "0 2 * * *", TimeZone = "Australia/Melbourne" };

		Assert.Equal( Utc( 2030, 6, 1, 16 ), RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 6, 1 ) ) );
	}

	[Fact]
	public void CronOccurrenceInDstGapJumpsForward()
	{
		// Melbourne DST starts Sunday 2030-10-06: 02:00 AEST jumps to 03:00 AEDT, so a 02:30
		// occurrence does not exist that day — Cronos fires at 03:00 AEDT = 16:00 UTC (05 Oct).
		Recurrence recurrence = new() { CronExpression = "30 2 * * *", TimeZone = "Australia/Melbourne" };

		Assert.Equal(
				Utc( 2030, 10, 5, 16 ),
				RecurrenceCalculator.GetFirstOccurrence( recurrence, Utc( 2030, 10, 5, 14 ) ) );
	}

	[Fact]
	public void CronNextAcrossADstTransitionKeepsLocalTime()
	{
		// New York DST starts 2030-03-10. Daily 09:00 local: EST (UTC-5) = 14:00 UTC on 9 March,
		// EDT (UTC-4) = 13:00 UTC on 10 March.
		Recurrence recurrence = new() { CronExpression = "0 9 * * *", TimeZone = "America/New_York" };

		Assert.Equal(
				Utc( 2030, 3, 10, 13 ),
				RecurrenceCalculator.GetNextOccurrence( recurrence, Utc( 2030, 3, 9, 14 ), Utc( 2030, 3, 9, 14, 30 ) ) );
	}

	// == Validation ==

	[Fact]
	public void RecurrenceMustSpecifyExactlyOneMode()
	{
		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate(
						new Recurrence
						{
							CronExpression = "0 2 * * *",
							Interval = TimeSpan.FromHours( 1 ),
							TimeZone = "Etc/UTC"
						} ) );

		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate( new Recurrence { TimeZone = "Etc/UTC" } ) );
	}

	[Fact]
	public void InvalidRecurrenceDetailsAreRejected()
	{
		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate(
						new Recurrence { Interval = TimeSpan.Zero, TimeZone = "Etc/UTC" } ) );

		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate(
						new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Neverland/Nowhere" } ) );

		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate(
						new Recurrence { CronExpression = "not cron", TimeZone = "Etc/UTC" } ) );

		Assert.Throws<ArgumentException>(
				() => RecurrenceCalculator.Validate(
						new Recurrence
						{
							Interval = TimeSpan.FromHours( 1 ),
							TimeZone = "Etc/UTC",
							NotBefore = new DateTime( 2030, 2, 1 ),
							NotAfter = new DateTime( 2030, 1, 1 )
						} ) );
	}
}
