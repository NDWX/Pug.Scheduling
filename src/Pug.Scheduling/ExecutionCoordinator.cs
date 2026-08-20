using Pug.Application.Data;
using Pug.Application.Data.Extensions;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Execution-side facade behind which all due-ness, misfire, retry, and next-occurrence
	// policy lives. Workers only see IExecutionCoordinator; exclusivity is arbitrated through
	// IExecutionLeaseProvider, never through the schedule store, so concurrent coordinator
	// instances may read overlapping due candidates and race safely for the lease.
	public class ExecutionCoordinator : IExecutionCoordinator
	{
		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IApplicationData<IDataSession> _applicationDataProvider;
		private readonly IExecutionLeaseProvider _leaseProvider;
		private readonly ExecutionCoordinatorOptions _options;

		public ExecutionCoordinator( IdentifierGenerator identifierGenerator,
									IApplicationData<IDataSession> applicationDataProvider,
									IExecutionLeaseProvider leaseProvider,
									ExecutionCoordinatorOptions options )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_applicationDataProvider = applicationDataProvider ??
										throw new ArgumentNullException( nameof(applicationDataProvider) );

			_leaseProvider = leaseProvider ?? throw new ArgumentNullException( nameof(leaseProvider) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );
		}

		private static void ValidateClaimant( string claimant )
		{
			if( string.IsNullOrWhiteSpace( claimant ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(claimant) );
		}

		public async Task<IEnumerable<ClaimedJob>> ClaimDueJobsAsync( string claimant, int maximumJobs )
		{
			ValidateClaimant( claimant );

			if( maximumJobs < 1 )
				throw new ArgumentException( "At least one job must be claimable.", nameof(maximumJobs) );

			IEnumerable<ScheduleInfo> candidates =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetDueSchedulesAsync( context.dueBy, context.maximumJobs )
											.ConfigureAwait( false ),
							context: new { dueBy = DateTime.UtcNow, maximumJobs }
						).ConfigureAwait( false );

			List<ClaimedJob> claimed = new();

			foreach( ScheduleInfo schedule in candidates )
			{
				if( claimed.Count >= maximumJobs )
					break;

				DateTime leaseExpiry = DateTime.UtcNow + _options.LeaseDuration;

				// Losing the race simply means a peer coordinator claimed this schedule.
				if( !await _leaseProvider.TryAcquireAsync( schedule.Identifier, claimant, leaseExpiry )
										.ConfigureAwait( false ) )
					continue;

				ClaimedJob job;

				try
				{
					job = await PrepareJobAsync( schedule, claimant, leaseExpiry ).ConfigureAwait( false );
				}
				catch
				{
					await _leaseProvider.ReleaseAsync( schedule.Identifier, claimant ).ConfigureAwait( false );

					throw;
				}

				if( job is not null )
					claimed.Add( job );
			}

			return claimed;
		}

		// Turns a claimed schedule into an executable job: resolves the occurrence being fired,
		// classifies the trigger, applies the misfire policy, and records the run. Returns null
		// when the occurrence is disposed of without execution (misfire SKIP or a stale read).
		private async Task<ClaimedJob> PrepareJobAsync( ScheduleInfo schedule, string claimant, DateTime leaseExpiry )
		{
			ScheduleDefinition definition = schedule.Definition;
			ScheduleState state = schedule.State;

			DateTime now = DateTime.UtcNow;

			// A concurrent completion may have advanced the schedule since the due-candidate
			// read; nothing to execute.
			if( state.NextRun is null || state.NextRun > now )
			{
				await _leaseProvider.ReleaseAsync( schedule.Identifier, claimant ).ConfigureAwait( false );

				return null;
			}

			DateTime due = DateTime.SpecifyKind( state.NextRun.Value, DateTimeKind.Utc );

			ScheduleRun latestRun =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetLatestRunAsync( context.schedule ).ConfigureAwait( false ),
							context: new { schedule = schedule.Identifier }
						).ConfigureAwait( false );

			DateTime scheduledFor;
			string trigger;

			if( state.RunRequested )
			{
				// An explicitly requested run executes immediately regardless of misfire policy.
				// A request during retry backoff accelerates the pending attempt, so the original
				// occurrence is preserved as the idempotency key.
				scheduledFor = state.Attempt > 0 && latestRun is not null ? latestRun.ScheduledFor : due;
				trigger = RunTriggers.Manual;
			}
			else if( state.Attempt > 0 && latestRun is not null )
			{
				// Retries re-execute the original occurrence: side-effect idempotency keys on
				// (Schedule, ScheduledFor) must not move between attempts.
				scheduledFor = latestRun.ScheduledFor;
				trigger = RunTriggers.Retry;
			}
			else
			{
				scheduledFor = due;

				bool misfired = now - due > definition.MisfirePolicy.Threshold;

				if( misfired && definition.MisfirePolicy.Handling == MisfireHandlings.Skip )
				{
					await SkipOccurrenceAsync( schedule, claimant, due, now ).ConfigureAwait( false );

					return null;
				}

				trigger = misfired ? RunTriggers.Misfire : RunTriggers.Schedule;
			}

			// Adopt the unfinished run of a lapsed claim (crashed worker) rather than inserting
			// a duplicate record of the same (schedule, occurrence, attempt).
			string runIdentifier;

			if( latestRun is not null && latestRun.Finished is null &&
				latestRun.ScheduledFor == scheduledFor && latestRun.Attempt == state.Attempt )
			{
				runIdentifier = latestRun.Identifier;

				if( state.RunRequested )
					await _applicationDataProvider.PerformAsync(
								async ( session, context ) =>
									await session.ClearRunRequestAsync( context.schedule ).ConfigureAwait( false ),
								context: new { schedule = schedule.Identifier }
							).ConfigureAwait( false );
			}
			else
			{
				runIdentifier = _identifierGenerator.GetNext();

				ScheduleRun run = new()
				{
					Identifier = runIdentifier,
					Schedule = schedule.Identifier,
					Domain = definition.Domain,
					ScheduledFor = scheduledFor,
					Started = now,
					Attempt = state.Attempt,
					Trigger = trigger
				};

				// The run request is consumed by the claim that honors it, in the same
				// transaction that records the run.
				await _applicationDataProvider.PerformAsync(
							async ( session, context ) =>
							{
								await session.InsertRunAsync( context.run ).ConfigureAwait( false );

								if( context.clearRunRequest )
									await session.ClearRunRequestAsync( context.schedule )
												.ConfigureAwait( false );
							},
							context: new
							{
								run,
								schedule = schedule.Identifier,
								clearRunRequest = state.RunRequested
							}
						).ConfigureAwait( false );
			}

			return new ClaimedJob
			{
				Context = new ScheduledJobContext
				{
					Schedule = schedule.Identifier,
					Run = runIdentifier,
					Domain = definition.Domain,
					Owner = schedule.Owner,
					JobType = definition.JobType,
					Payload = definition.Payload,
					ScheduledFor = scheduledFor,
					Attempt = state.Attempt,
					Trigger = trigger
				},
				Timeout = definition.Timeout,
				ClaimExpiry = leaseExpiry
			};
		}

		// Misfire SKIP: dispose of the missed occurrence without execution — record a SKIPPED
		// run for the audit trail, advance to the next future occurrence, release the claim.
		private async Task SkipOccurrenceAsync( ScheduleInfo schedule, string claimant, DateTime due, DateTime now )
		{
			DateTime? nextRun = RecurrenceCalculator.GetNextOccurrence( schedule.Definition.Recurrence, now );

			ScheduleRun run = new()
			{
				Identifier = _identifierGenerator.GetNext(),
				Schedule = schedule.Identifier,
				Domain = schedule.Definition.Domain,
				ScheduledFor = due,
				Started = now,
				Finished = now,
				Attempt = 0,
				Trigger = RunTriggers.Misfire,
				Result = RunResults.Skipped
			};

			RunSummary lastRun = new() { Started = now, Finished = now, Result = RunResults.Skipped };

			await _applicationDataProvider.PerformAsync(
						async ( session, context ) =>
						{
							await session.InsertRunAsync( context.run ).ConfigureAwait( false );

							await session.RecordOccurrenceCompletionAsync(
											context.schedule, 0, context.nextRun, context.lastRun
										)
										.ConfigureAwait( false );
						},
						context: new { run, schedule = schedule.Identifier, nextRun, lastRun }
					).ConfigureAwait( false );

			await _leaseProvider.ReleaseAsync( schedule.Identifier, claimant ).ConfigureAwait( false );
		}

		public async Task<bool> RenewClaimAsync( string run, string claimant )
		{
			ValidateClaimant( claimant );

			if( string.IsNullOrWhiteSpace( run ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(run) );

			ScheduleRun runRecord =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetRunAsync( context.run ).ConfigureAwait( false ),
							context: new { run }
						).ConfigureAwait( false );

			if( runRecord is null )
				return false;

			return await _leaseProvider.RenewAsync(
							runRecord.Schedule, claimant, DateTime.UtcNow + _options.LeaseDuration )
						.ConfigureAwait( false );
		}

		public async Task ReportCompletionAsync( string run, string claimant, string result, JobOutcome outcome )
		{
			ValidateClaimant( claimant );

			if( string.IsNullOrWhiteSpace( run ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(run) );

			// SKIPPED is the coordinator's own classification; workers report executed runs.
			if( result != RunResults.Succeeded && result != RunResults.Failed && result != RunResults.TimedOut )
				throw new ArgumentException( $"Unknown or non-reportable run result '{result}'.", nameof(result) );

			ScheduleRun runRecord =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetRunAsync( context.run ).ConfigureAwait( false ),
							context: new { run }
						).ConfigureAwait( false );

			if( runRecord is null )
				throw new UnknownRunException( run );

			ScheduleInfo schedule =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetScheduleAsync( context.schedule ).ConfigureAwait( false ),
							context: new { schedule = runRecord.Schedule }
						).ConfigureAwait( false );

			if( schedule is null )
				throw new UnknownScheduleException( runRecord.Schedule );

			DateTime now = DateTime.UtcNow;

			// RUN_ALL executes every missed occurrence, so the next occurrence follows the one
			// just executed; other policies coalesce and continue from now.
			DateTime occurrenceBaseline =
				schedule.Definition.MisfirePolicy.Handling == MisfireHandlings.RunAll
					? DateTime.SpecifyKind( runRecord.ScheduledFor, DateTimeKind.Utc )
					: now;

			int attempt;
			DateTime? nextRun;

			if( result == RunResults.Succeeded )
			{
				attempt = 0;
				nextRun = RecurrenceCalculator.GetNextOccurrence( schedule.Definition.Recurrence, occurrenceBaseline );
			}
			else
			{
				int nextAttempt = runRecord.Attempt + 1;

				if( nextAttempt < schedule.Definition.RetryPolicy.MaximumAttempts )
				{
					// Exponential backoff: Backoff * 2^(retry - 1) from now.
					attempt = nextAttempt;
					nextRun = now + schedule.Definition.RetryPolicy.Backoff * Math.Pow( 2, nextAttempt - 1 );
				}
				else
				{
					// Terminal failure of this occurrence: record it and advance.
					attempt = 0;
					nextRun = RecurrenceCalculator.GetNextOccurrence(
									schedule.Definition.Recurrence, occurrenceBaseline );
				}
			}

			RunSummary lastRun = new() { Started = runRecord.Started, Finished = now, Result = result };

			await _applicationDataProvider.PerformAsync(
						async ( session, context ) =>
						{
							await session.CompleteRunAsync(
											context.run, context.now, context.result, context.outcome
										)
										.ConfigureAwait( false );

							await session.RecordOccurrenceCompletionAsync(
											context.schedule, context.attempt, context.nextRun, context.lastRun
										)
										.ConfigureAwait( false );
						},
						context: new { run, now, result, outcome, schedule = schedule.Identifier, attempt, nextRun, lastRun }
					).ConfigureAwait( false );

			await _leaseProvider.ReleaseAsync( schedule.Identifier, claimant ).ConfigureAwait( false );
		}
	}
}
