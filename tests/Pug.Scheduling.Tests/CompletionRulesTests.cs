using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;
using static Pug.Scheduling.Tests.OccurrenceStateTests;

namespace Pug.Scheduling.Tests;

// CompletionRules and DefinitionRules: how occurrence progress follows a completion, a skip or a
// definition update. Pure functions, no store.
public class CompletionRulesTests
{
	private static readonly DateTime Now = new( 2031, 5, 6, 12, 0, 0, DateTimeKind.Utc );

	private static ScheduleRun Run( DateTime scheduledFor, int attempt = 0, string trigger = RunTriggers.Schedule,
									DateTime? started = null ) =>
		new()
		{
			Identifier = "run-1",
			Schedule = "schedule-1",
			Domain = "tenant-1",
			ScheduledFor = scheduledFor,
			Started = started ?? Now.AddMinutes( -1 ),
			Attempt = attempt,
			Trigger = trigger
		};

	[Fact]
	public void SuccessEndsTheOccurrenceAndAdvancesOnTheGrid()
	{
		DateTime occurrence = Now.AddMinutes( -2 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( occurrence ), Run( occurrence ), RunResults.Succeeded, Now );

		Assert.Equal( 0, progress.Attempt );
		Assert.Null( progress.Occurrence );
		Assert.Equal( occurrence.AddHours( 1 ), progress.NextRun );
		Assert.Equal( new RunSummary { Started = Now.AddMinutes( -1 ), Finished = Now, Result = RunResults.Succeeded },
					progress.LastRun );
		Assert.False( progress.RunRequested );
	}

	[Fact]
	public void LateCompletionKeepsTheGridPhaseAndCoalescesMissedSlots()
	{
		DateTime occurrence = Now.AddMinutes( -150 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( occurrence ), Run( occurrence ), RunResults.Succeeded, Now );

		// Slots at -90 and -30 minutes were missed; the next is +30, not Now + 1h.
		Assert.Equal( Now.AddMinutes( 30 ), progress.NextRun );
	}

	[Fact]
	public void RunAllAdvancesFromTheOccurrenceItExecuted()
	{
		DateTime occurrence = Now.AddHours( -3 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( occurrence, misfireHandling: MisfireHandlings.RunAll ), Run( occurrence ), RunResults.Succeeded,
				Now );

		Assert.Equal( occurrence.AddHours( 1 ), progress.NextRun );
	}

	[Theory]
	[InlineData( RunResults.Failed )]
	[InlineData( RunResults.TimedOut )]
	public void FailureRetriesTheSameOccurrenceWithExponentialBackoff( string result )
	{
		DateTime occurrence = Now.AddMinutes( -2 );

		OccurrenceProgress first = CompletionRules.Next( Schedule( occurrence ), Run( occurrence ), result, Now );

		Assert.Equal( 1, first.Attempt );
		Assert.Equal( occurrence, first.Occurrence );
		Assert.Equal( Now.AddMinutes( 1 ), first.NextRun );

		OccurrenceProgress second = CompletionRules.Next(
				Schedule( first.NextRun, 1, occurrence ), Run( occurrence, 1, RunTriggers.Retry ), result, Now );

		Assert.Equal( 2, second.Attempt );
		Assert.Equal( occurrence, second.Occurrence );
		Assert.Equal( Now.AddMinutes( 2 ), second.NextRun );
	}

	[Fact]
	public void FailureWithNoAttemptLeftEndsTheOccurrence()
	{
		DateTime occurrence = Now.AddMinutes( -10 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( Now, 2, occurrence ), Run( occurrence, 2, RunTriggers.Retry ), RunResults.Failed, Now );

		Assert.Equal( 0, progress.Attempt );
		Assert.Null( progress.Occurrence );
		Assert.Equal( occurrence.AddHours( 1 ), progress.NextRun );
		Assert.Equal( RunResults.Failed, progress.LastRun!.Result );
	}

	[Theory]
	[InlineData( RunResults.Succeeded )]
	[InlineData( RunResults.Failed )]
	public void ManualRunLeavesAFutureOccurrenceAndIsNeverRetried( string result )
	{
		DateTime nextRun = Now.AddMinutes( 30 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( nextRun ), Run( Now.AddMinutes( -1 ), trigger: RunTriggers.Manual ), result, Now );

		Assert.Equal( 0, progress.Attempt );
		Assert.Null( progress.Occurrence );
		Assert.Equal( nextRun, progress.NextRun );
		Assert.Equal( result, progress.LastRun!.Result );
	}

	[Theory]
	[InlineData( MisfireHandlings.FireOnce, true )]
	[InlineData( MisfireHandlings.Skip, true )]
	[InlineData( MisfireHandlings.RunAll, false )]
	public void ManualRunCoalescesAnOccurrenceDueWhenItStartedExceptUnderRunAll( string handling, bool coalesced )
	{
		DateTime backlog = Now.AddHours( -3 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( backlog, misfireHandling: handling ),
				Run( Now.AddMinutes( -1 ), trigger: RunTriggers.Manual, started: Now.AddMinutes( -1 ) ),
				RunResults.Succeeded, Now );

		Assert.Equal( coalesced ? Now.AddHours( 1 ) : backlog, progress.NextRun );
	}

	[Fact]
	public void ManualRunLeavesAnOccurrenceThatFellDueWhileItRan()
	{
		DateTime nextRun = Now.AddSeconds( -10 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( nextRun ),
				Run( Now.AddMinutes( -1 ), trigger: RunTriggers.Manual, started: Now.AddMinutes( -1 ) ),
				RunResults.Succeeded, Now );

		Assert.Equal( nextRun, progress.NextRun );
	}

	// A manual run during a retry's backoff carries the retry's attempt: it is that retry.
	[Fact]
	public void ManualRunDuringBackoffCountsAsTheRetry()
	{
		DateTime occurrence = Now.AddMinutes( -5 );

		OccurrenceProgress progress = CompletionRules.Next(
				Schedule( Now.AddMinutes( 1 ), 1, occurrence ), Run( occurrence, 1, RunTriggers.Manual ), RunResults.Failed,
				Now );

		Assert.Equal( 2, progress.Attempt );
		Assert.Equal( occurrence, progress.Occurrence );
		Assert.Equal( Now.AddMinutes( 2 ), progress.NextRun );
	}

	[Fact]
	public void RunRequestIsCarriedNeverConsumed()
	{
		DateTime occurrence = Now.AddMinutes( -2 );

		Assert.True( CompletionRules.Next(
							Schedule( occurrence, runRequested: true ), Run( occurrence ), RunResults.Succeeded, Now )
						.RunRequested );

		Assert.True( CompletionRules.Skipped( Schedule( occurrence, runRequested: true ), occurrence, Now ).RunRequested );
	}

	[Fact]
	public void SkipAdvancesOntoTheGridAndRecordsTheSkip()
	{
		DateTime occurrence = Now.AddHours( -2 );

		OccurrenceProgress progress = CompletionRules.Skipped(
				Schedule( occurrence, misfireHandling: MisfireHandlings.Skip ), occurrence, Now );

		Assert.Equal( 0, progress.Attempt );
		Assert.Null( progress.Occurrence );
		Assert.Equal( Now.AddHours( 1 ), progress.NextRun );
		Assert.Equal( new RunSummary { Started = Now, Finished = Now, Result = RunResults.Skipped }, progress.LastRun );
	}

	// #8: an unchanged recurrence keeps whatever next occurrence the (locked) schedule has.
	[Fact]
	public void UpdateWithUnchangedRecurrenceKeepsTheNextRun()
	{
		ScheduleInfo schedule = Schedule( Now.AddMinutes( 17 ) );

		Assert.Equal(
				Now.AddMinutes( 17 ),
				DefinitionRules.NextRunAfterUpdate( schedule, schedule.Definition with { Name = "Renamed" }, Now ) );
	}

	[Fact]
	public void UpdateWithChangedRecurrenceRestartsFromNowUnlessARetryIsPending()
	{
		ScheduleInfo idle = Schedule( Now.AddMinutes( 17 ) );
		ScheduleDefinition threeHourly = idle.Definition with
		{
			Recurrence = idle.Definition.Recurrence with { Interval = TimeSpan.FromHours( 3 ) }
		};

		Assert.Equal( Now.AddHours( 3 ), DefinitionRules.NextRunAfterUpdate( idle, threeHourly, Now ) );

		ScheduleInfo retrying = Schedule( Now.AddMinutes( 1 ), 1, Now.AddMinutes( -5 ) );

		Assert.Equal( Now.AddMinutes( 1 ), DefinitionRules.NextRunAfterUpdate( retrying, threeHourly, Now ) );
	}

	[Fact]
	public void UpdateWithChangedRecurrenceDuringARunKeepsTheNextRun()
	{
		ScheduleInfo running = Schedule(
				Now.AddMinutes( -1 ),
				lease: Lease( "worker-1/1", Now.AddMinutes( 5 ) ) );
		ScheduleDefinition threeHourly = running.Definition with
		{
			Recurrence = running.Definition.Recurrence with { Interval = TimeSpan.FromHours( 3 ) }
		};

		Assert.Equal( Now.AddMinutes( -1 ), DefinitionRules.NextRunAfterUpdate( running, threeHourly, Now ) );
	}
}
