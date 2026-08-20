using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests;

public class RecurrenceCalculatorTests
{
	[Fact]
	public void IntervalRecurrenceAddsIntervalToBaseline()
	{
		Recurrence recurrence = new() { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" };

		DateTime? next = RecurrenceCalculator.GetNextOccurrence(
				recurrence, new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc ) );

		Assert.Equal( new DateTime( 2030, 1, 1, 1, 0, 0, DateTimeKind.Utc ), next );
	}

	[Fact]
	public void NotBeforeClampsTheBaseline()
	{
		Recurrence recurrence = new()
		{
			Interval = TimeSpan.FromHours( 1 ),
			TimeZone = "Etc/UTC",
			NotBefore = new DateTime( 2030, 2, 1, 0, 0, 0, DateTimeKind.Utc )
		};

		DateTime? next = RecurrenceCalculator.GetNextOccurrence(
				recurrence, new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc ) );

		Assert.Equal( new DateTime( 2030, 2, 1, 1, 0, 0, DateTimeKind.Utc ), next );
	}

	[Fact]
	public void RecurrenceExhaustsPastNotAfter()
	{
		Recurrence recurrence = new()
		{
			Interval = TimeSpan.FromHours( 1 ),
			TimeZone = "Etc/UTC",
			NotAfter = new DateTime( 2030, 1, 1, 0, 30, 0, DateTimeKind.Utc )
		};

		DateTime? next = RecurrenceCalculator.GetNextOccurrence(
				recurrence, new DateTime( 2030, 1, 1, 0, 0, 0, DateTimeKind.Utc ) );

		Assert.Null( next );
	}

	[Fact]
	public void CronOccurrencesFollowTheRecurrenceTimeZone()
	{
		// Daily 02:00 Melbourne time; June = AEST (UTC+10), so 02:00 local = 16:00 UTC previous day.
		Recurrence recurrence = new() { CronExpression = "0 2 * * *", TimeZone = "Australia/Melbourne" };

		DateTime? next = RecurrenceCalculator.GetNextOccurrence(
				recurrence, new DateTime( 2030, 6, 1, 0, 0, 0, DateTimeKind.Utc ) );

		Assert.Equal( new DateTime( 2030, 6, 1, 16, 0, 0, DateTimeKind.Utc ), next );
	}

	[Fact]
	public void CronOccurrenceInDstGapJumpsForward()
	{
		// Melbourne DST starts Sunday 2030-10-06: 02:00 AEST jumps to 03:00 AEDT, so a 02:30
		// occurrence does not exist that day — Cronos fires at 03:00 AEDT = 16:00 UTC (05 Oct).
		Recurrence recurrence = new() { CronExpression = "30 2 * * *", TimeZone = "Australia/Melbourne" };

		DateTime? next = RecurrenceCalculator.GetNextOccurrence(
				recurrence, new DateTime( 2030, 10, 5, 14, 0, 0, DateTimeKind.Utc ) );

		Assert.Equal( new DateTime( 2030, 10, 5, 16, 0, 0, DateTimeKind.Utc ), next );
	}

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
