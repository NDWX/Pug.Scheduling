using Pug.Effable;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests;

// OccurrenceState's classification and run derivation: pure functions, no store.
public class OccurrenceStateTests
{
	private const string Holder = "group/1";

	private static readonly DateTime Now = new( 2031, 5, 6, 12, 0, 0, DateTimeKind.Utc );

	internal static ScheduleInfo Schedule( DateTime? nextRun, int attempt = 0, DateTime? occurrence = null,
											bool runRequested = false, ExecutionLease? lease = null,
											string misfireHandling = MisfireHandlings.FireOnce, bool enabled = true,
											bool paused = false, int maximumAttempts = 3 ) =>
		new()
		{
			Identifier = "schedule-1",
			Owner = new Reference { Type = "USER", Identifier = "owner-1" },
			Definition = new ScheduleDefinition
			{
				Domain = "tenant-1",
				Name = "Rules",
				JobType = "test",
				Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" },
				MisfirePolicy = new MisfirePolicy { Handling = misfireHandling, Threshold = TimeSpan.FromMinutes( 5 ) },
				RetryPolicy = new RetryPolicy { MaximumAttempts = maximumAttempts, Backoff = TimeSpan.FromMinutes( 1 ) },
				Timeout = TimeSpan.FromMinutes( 10 )
			},
			State = new ScheduleState
			{
				Enabled = enabled,
				Paused = paused,
				NextRun = nextRun,
				RunRequested = runRequested,
				Attempt = attempt,
				Occurrence = occurrence
			},
			ExecutionLease = lease
		};

	private static ExecutionLease Lease( string holder, DateTime expiry ) =>
		new() { Run = "run-1", ClaimedBy = holder, Expiry = expiry };

	// The action is an object: xunit needs public signatures, and OccurrenceAction is internal.
	[Theory]
	[InlineData( 10, OccurrenceAction.NotDue )] // not yet due
	[InlineData( 0, OccurrenceAction.Schedule )] // due right now
	[InlineData( -5 * 60, OccurrenceAction.Schedule )] // overdue by exactly the threshold
	[InlineData( -5 * 60 - 1, OccurrenceAction.Misfire )] // beyond it
	public void IdleScheduleIsClassifiedByItsNextOccurrence( int dueInSeconds, object expected )
	{
		Assert.Equal( (OccurrenceAction)expected, OccurrenceState.Classify( Schedule( Now.AddSeconds( dueInSeconds ) ), Now, Holder ) );
	}

	[Theory]
	[InlineData( MisfireHandlings.FireOnce, OccurrenceAction.Misfire )]
	[InlineData( MisfireHandlings.RunAll, OccurrenceAction.Misfire )]
	[InlineData( MisfireHandlings.Skip, OccurrenceAction.Skip )]
	public void MisfiredOccurrenceFollowsThePolicy( string handling, object expected )
	{
		Assert.Equal(
				(OccurrenceAction)expected,
				OccurrenceState.Classify( Schedule( Now.AddHours( -1 ), misfireHandling: handling ), Now, Holder ) );
	}

	[Fact]
	public void ExhaustedRecurrenceIsNeverDue()
	{
		Assert.Equal( OccurrenceAction.NotDue, OccurrenceState.Classify( Schedule( null ), Now, Holder ) );
	}

	[Fact]
	public void PausedOrDisabledScheduleIsNeverClaimed()
	{
		ExecutionLease lapsed = Lease( "peer/1", Now.AddMinutes( -1 ) );

		Assert.Equal( OccurrenceAction.NotDue,
					OccurrenceState.Classify( Schedule( Now.AddHours( -1 ), paused: true ), Now, Holder ) );
		Assert.Equal( OccurrenceAction.NotDue,
					OccurrenceState.Classify( Schedule( Now.AddHours( -1 ), enabled: false, runRequested: true ), Now, Holder ) );
		Assert.Equal( OccurrenceAction.NotDue,
					OccurrenceState.Classify( Schedule( Now, paused: true, lease: lapsed ), Now, Holder ) );
	}

	[Fact]
	public void ActiveRunIsAdoptedWhenLapsedOrOwnAndOtherwiseLeftAlone()
	{
		DateTime due = Now.AddMinutes( -1 );

		Assert.Equal( OccurrenceAction.NotDue,
					OccurrenceState.Classify( Schedule( due, lease: Lease( "peer/1", Now.AddSeconds( 1 ) ) ), Now, Holder ) );

		// Expiry is inclusive: a lease that expires now has lapsed.
		Assert.Equal( OccurrenceAction.Adopt,
					OccurrenceState.Classify( Schedule( due, lease: Lease( "peer/1", Now ) ), Now, Holder ) );

		// A restarted holder re-takes its own claim while it is live.
		Assert.Equal( OccurrenceAction.Adopt,
					OccurrenceState.Classify( Schedule( due, lease: Lease( Holder, Now.AddMinutes( 1 ) ) ), Now, Holder ) );
	}

	// The precedence that fixes #7: a run in progress is adopted before any rule that would record
	// its occurrence again — a misfire SKIP, a request, a retry.
	[Fact]
	public void AdoptionTakesPrecedenceOverEveryOtherRule()
	{
		ExecutionLease lapsed = Lease( "peer/1", Now.AddMinutes( -1 ) );

		Assert.Equal( OccurrenceAction.Adopt,
					OccurrenceState.Classify(
							Schedule( Now.AddHours( -1 ), misfireHandling: MisfireHandlings.Skip, lease: lapsed ), Now, Holder ) );

		Assert.Equal( OccurrenceAction.Adopt,
					OccurrenceState.Classify( Schedule( Now.AddHours( 1 ), runRequested: true, lease: lapsed ), Now, Holder ) );

		Assert.Equal( OccurrenceAction.Adopt,
					OccurrenceState.Classify(
							Schedule( Now.AddHours( -1 ), attempt: 1, occurrence: Now.AddHours( -2 ), lease: lapsed ), Now,
							Holder ) );
	}

	[Fact]
	public void RequestIsHonoredWhateverTheNextOccurrenceAndBeforeARetry()
	{
		Assert.Equal( OccurrenceAction.Manual,
					OccurrenceState.Classify( Schedule( Now.AddHours( 1 ), runRequested: true ), Now, Holder ) );
		Assert.Equal( OccurrenceAction.Manual,
					OccurrenceState.Classify( Schedule( null, runRequested: true ), Now, Holder ) );
		Assert.Equal( OccurrenceAction.Manual,
					OccurrenceState.Classify(
							Schedule( Now.AddMinutes( 1 ), attempt: 1, occurrence: Now.AddMinutes( -1 ), runRequested: true ),
							Now, Holder ) );
	}

	[Fact]
	public void PendingRetryIsClaimedWhenItsTimeComesAndNeverMisfires()
	{
		DateTime occurrence = Now.AddHours( -3 );

		Assert.Equal( OccurrenceAction.NotDue,
					OccurrenceState.Classify( Schedule( Now.AddSeconds( 1 ), attempt: 1, occurrence: occurrence ), Now, Holder ) );

		Assert.Equal( OccurrenceAction.Retry,
					OccurrenceState.Classify(
							Schedule( Now.AddHours( -1 ), attempt: 2, occurrence: occurrence,
									misfireHandling: MisfireHandlings.Skip ), Now, Holder ) );
	}

	[Fact]
	public void RunIsRecordedUnderTheOccurrenceItExecutes()
	{
		DateTime nextRun = Now.AddMinutes( -2 );
		DateTime occurrence = Now.AddHours( -1 );

		Assert.Equal( nextRun, OccurrenceState.ScheduledFor( Schedule( nextRun ), OccurrenceAction.Schedule, Now ) );
		Assert.Equal( nextRun, OccurrenceState.ScheduledFor( Schedule( nextRun ), OccurrenceAction.Misfire, Now ) );
		Assert.Equal( nextRun, OccurrenceState.ScheduledFor( Schedule( nextRun ), OccurrenceAction.Skip, Now ) );

		// A retry, and a manual run accelerating one, keep the original occurrence.
		Assert.Equal( occurrence,
					OccurrenceState.ScheduledFor( Schedule( nextRun, 1, occurrence ), OccurrenceAction.Retry, Now ) );
		Assert.Equal( occurrence,
					OccurrenceState.ScheduledFor( Schedule( nextRun, 1, occurrence ), OccurrenceAction.Manual, Now ) );

		// Any other manual run is for the time it was claimed.
		Assert.Equal( Now, OccurrenceState.ScheduledFor( Schedule( nextRun ), OccurrenceAction.Manual, Now ) );

		// A retry recorded before the occurrence was carried can only be keyed by its retry time.
		Assert.Equal( nextRun, OccurrenceState.ScheduledFor( Schedule( nextRun, 1 ), OccurrenceAction.Retry, Now ) );
	}

	[Theory]
	[InlineData( OccurrenceAction.Manual, RunTriggers.Manual )]
	[InlineData( OccurrenceAction.Retry, RunTriggers.Retry )]
	[InlineData( OccurrenceAction.Misfire, RunTriggers.Misfire )]
	[InlineData( OccurrenceAction.Skip, RunTriggers.Misfire )]
	[InlineData( OccurrenceAction.Schedule, RunTriggers.Schedule )]
	public void TriggerFollowsTheAction( object action, string trigger )
	{
		Assert.Equal( trigger, OccurrenceState.Trigger( (OccurrenceAction)action ) );
	}

	[Fact]
	public void NoRunIsStartedForNotDueOrAdopt()
	{
		Assert.Throws<ArgumentOutOfRangeException>( () => OccurrenceState.Trigger( OccurrenceAction.Adopt ) );
		Assert.Throws<ArgumentOutOfRangeException>(
				() => OccurrenceState.ScheduledFor( Schedule( Now ), OccurrenceAction.NotDue, Now ) );
	}
}
