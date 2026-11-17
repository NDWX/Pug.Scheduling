using Pug.Effable;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker;

namespace Pug.Scheduling.Tests;

// The worker's store of successful outcomes it could not get recorded (D29): keyed by occurrence,
// bounded by retention and capacity, and never replaying under a changed definition.
public class UnrecordedSuccessesTests
{
	private static readonly DateTime Occurrence = new( 2030, 1, 2, 3, 4, 5, DateTimeKind.Utc );

	private static readonly JobOutcome Success = new() { Successful = true, Details = /*lang=json*/ """{"sent":true}""" };

	private static ScheduledJobContext Context( string schedule = "schedule-1", DateTime? scheduledFor = null,
												string run = "run-1", string jobType = "report",
												string payload = "payload" ) =>
		new()
		{
			Schedule = schedule,
			Run = run,
			Domain = "tenant-1",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			JobType = jobType,
			Payload = payload,
			ScheduledFor = scheduledFor ?? Occurrence,
			Attempt = 0,
			Trigger = RunTriggers.Schedule
		};

	[Fact]
	public void ARememberedOutcomeIsFoundForAnyRunOfTheOccurrenceUntilForgotten()
	{
		UnrecordedSuccesses store = new();

		store.Remember( Context(), Success );

		Assert.True( store.TryGet( Context( run: "run-2" ), out JobOutcome outcome, out bool changed ) );
		Assert.Same( Success, outcome );
		Assert.False( changed );

		Assert.False( store.TryGet( Context( schedule: "schedule-2" ), out _, out _ ) );
		Assert.False( store.TryGet( Context( scheduledFor: Occurrence.AddSeconds( 1 ) ), out _, out _ ) );

		store.Forget( Context() );

		Assert.False( store.TryGet( Context(), out _, out changed ) );
		Assert.False( changed );
	}

	[Fact]
	public void TheOccurrenceIsMatchedInUtcToTheMicrosecond()
	{
		UnrecordedSuccesses store = new();

		// Remembered from a first claim carrying 100 ns ticks...
		store.Remember( Context( scheduledFor: Occurrence.AddTicks( 7 ) ), Success );

		// ...found from an adoption read back from Postgres, at whole microseconds, and in the other
		// kinds a transport may hand back.
		Assert.True( store.TryGet( Context( scheduledFor: Occurrence ), out _, out _ ) );
		// A Local value carries no offset of its own: it means the host's zone, which is what the
		// store converts through. So it is built from the host's zone too; a value built from any
		// other offset would be a different instant. On a host at UTC the conversion is the identity,
		// and only the Kind differs.
		DateTime local = Occurrence.ToLocalTime();

		Assert.Equal( DateTimeKind.Local, local.Kind );
		Assert.True( store.TryGet( Context( scheduledFor: local ), out _, out _ ) );
		Assert.True( store.TryGet( Context( scheduledFor: DateTime.SpecifyKind( Occurrence, DateTimeKind.Unspecified ) ),
									out _, out _ ) );

		// The next microsecond is another occurrence.
		Assert.False( store.TryGet( Context( scheduledFor: Occurrence.AddTicks( 10 ) ), out _, out _ ) );
	}

	[Theory]
	[InlineData( "other-type", "payload" )]
	[InlineData( "report", "other-payload" )]
	public void AChangedDefinitionIsNotReplayedAndIsDropped( string jobType, string payload )
	{
		UnrecordedSuccesses store = new();

		store.Remember( Context(), Success );

		Assert.False( store.TryGet( Context( jobType: jobType, payload: payload ), out JobOutcome outcome, out bool changed ) );
		Assert.Null( outcome );
		Assert.True( changed );

		// Dropped: not found even under the original definition.
		Assert.False( store.TryGet( Context(), out _, out changed ) );
		Assert.False( changed );
	}

	[Fact]
	public void EntriesExpireAfterTheRetentionCountedFromTheFirstRemember()
	{
		ManualTimeProvider clock = new( Occurrence );

		UnrecordedSuccesses store = new( clock );

		store.Remember( Context(), Success );

		clock.Advance( UnrecordedSuccesses.Retention - TimeSpan.FromSeconds( 1 ) );

		// Remembering again keeps the original time.
		store.Remember( Context(), Success );

		Assert.True( store.TryGet( Context(), out _, out _ ) );

		clock.Advance( TimeSpan.FromSeconds( 1 ) );

		Assert.False( store.TryGet( Context(), out _, out _ ) );
	}

	[Fact]
	public void BeyondTheCapacityTheOldestIsEvicted()
	{
		UnrecordedSuccesses store = new();

		for( int occurrence = 0; occurrence <= UnrecordedSuccesses.Capacity; occurrence++ )
			store.Remember( Context( scheduledFor: Occurrence.AddSeconds( occurrence ) ), Success );

		Assert.False( store.TryGet( Context( scheduledFor: Occurrence ), out _, out _ ) );
		Assert.True( store.TryGet( Context( scheduledFor: Occurrence.AddSeconds( 1 ) ), out _, out _ ) );
		Assert.True( store.TryGet( Context( scheduledFor: Occurrence.AddSeconds( UnrecordedSuccesses.Capacity ) ),
									out _, out _ ) );
	}
}
