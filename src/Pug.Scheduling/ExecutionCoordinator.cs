using System.Transactions;
using Microsoft.Extensions.Logging;
using Pug.Application.Data;
using Pug.Application.Data.Extensions;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Execution-side facade behind which all due-ness, misfire, retry, and next-occurrence
	// policy lives. Workers only see IExecutionCoordinator; exclusivity is arbitrated through
	// IExecutionLeaseProvider, never through the schedule store, so concurrent coordinator
	// instances may read overlapping due candidates and race safely for the lease.
	//
	// Authorization lives here rather than in any hosting surface, so it cannot be bypassed by
	// binding the coordinator differently. Every call carrying a security principal requires the
	// corresponding CoordinatorSecurityOperations grant on the claimant identity being acted as,
	// which is what keeps one worker from claiming, renewing, or completing runs as another;
	// calls with no principal at all are in-process by construction and are trusted.
	//
	// Leases are held per worker, as "claimant/worker": grants belong to the group (claimant),
	// exclusivity to the individual worker. A worker restarted under the same claimant and
	// number presents the same holder, so the lease store lets it re-take its predecessor's live
	// lease, and PrepareJobAsync adopts the unfinished run rather than duplicating it.
	public class ExecutionCoordinator : IExecutionCoordinator
	{
		// Every schedule-store unit is either a single read or writes only, never a read the same
		// transaction then acts on: the races between writers are settled by single-statement
		// conditions (a run is completed or adopted only while unfinished) and by the execution
		// lease guarding the schedule. Those are READ COMMITTED semantics, as for the lease store
		// (D21, D22). The data extensions' default scope is SERIALIZABLE, under which Postgres
		// fails the losing side of a race (40001) — e.g. a late completion racing an adoption of
		// the same run — instead of letting its conditional write affect no rows. Each unit
		// commits on its own, never inside a caller's transaction.
		private static readonly TransactionOptions ScheduleStoreTransaction =
			new() { IsolationLevel = IsolationLevel.ReadCommitted };

		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IApplicationData<IDataSession> _applicationDataProvider;
		private readonly IExecutionLeaseProvider _leaseProvider;
		private readonly ISecurityManager _securityManager;
		private readonly ExecutionCoordinatorOptions _options;
		private readonly ILogger<ExecutionCoordinator> _logger;

		public ExecutionCoordinator( IdentifierGenerator identifierGenerator,
									IApplicationData<IDataSession> applicationDataProvider,
									IExecutionLeaseProvider leaseProvider,
									ISecurityManager securityManager,
									ExecutionCoordinatorOptions options,
									ILogger<ExecutionCoordinator> logger )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_applicationDataProvider = applicationDataProvider ??
										throw new ArgumentNullException( nameof(applicationDataProvider) );

			_leaseProvider = leaseProvider ?? throw new ArgumentNullException( nameof(leaseProvider) );

			_securityManager = securityManager ?? throw new ArgumentNullException( nameof(securityManager) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );

			if( _options.ClaimCandidates < 1 )
				throw new ArgumentException( "At least one claim candidate is required.", nameof(options) );

			_logger = logger ?? throw new ArgumentNullException( nameof(logger) );
		}

		// claimant is the worker group's identity, qualifying its grants; the lease holder is the
		// individual worker within it, composed as "claimant/worker". The separator is reserved
		// so no two (claimant, worker) pairs compose to the same holder.
		private static string ValidateCaller( string claimant, int worker )
		{
			if( string.IsNullOrWhiteSpace( claimant ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(claimant) );

			if( claimant.Contains( '/' ) )
				throw new ArgumentException( "Claimant must not contain '/'.", nameof(claimant) );

			if( worker < 1 )
				throw new ArgumentException( "Worker number must be 1 or greater.", nameof(worker) );

			return $"{claimant}/{worker}";
		}

		// Grants are qualified by the claimant identity being acted as; there is no owner rule
		// here — execution is an operator concern, not the schedule owner's.
		//
		// The absence of a principal identifies an in-process caller and is trusted: every
		// endpoint in front of the coordinator requires an authenticated caller before dispatch,
		// so a call that crossed a transport always arrives with a principal and is always
		// checked. A worker hosted in-process therefore needs no service identity of its own.
		private async Task CheckAuthorizationAsync( string operation, string claimant, int worker )
		{
			if( _securityManager.CurrentPrincipal is null )
				return;

			bool isAuthorized = await _securityManager.CurrentPrincipal
													.IsAuthorizedAsync(
															new Dictionary<string, string>( 0 ),
															operation,
															ResourceQualifiers.ExecutionCoordinator( claimant ),
															string.Empty
														)
													.ConfigureAwait( false );

			if( isAuthorized )
				return;

			_logger.LogWarning(
					"Authorization denied: {Operation} as claimant {Claimant} (worker {Worker}).",
					operation, claimant, worker );

			throw new NotAuthorized();
		}

		public async Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType )
		{
			string holder = ValidateCaller( claimant, worker );

			if( jobType is not null && string.IsNullOrWhiteSpace( jobType ) )
				throw new ArgumentException( "Value cannot be whitespace; pass null for any job type.", nameof(jobType) );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.ClaimJobs, claimant, worker )
				.ConfigureAwait( false );

			// A small window rather than the single oldest due schedule: losing the race for one
			// candidate to a peer moves on to the next, instead of returning nothing while other
			// work is due.
			IEnumerable<ScheduleInfo> candidates =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetDueSchedulesAsync(
												context.dueBy, context.jobType, context.maximumSchedules )
											.ConfigureAwait( false ),
							context: new { dueBy = DateTime.UtcNow, jobType, maximumSchedules = _options.ClaimCandidates },
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );

			foreach( ScheduleInfo schedule in candidates )
			{
				DateTime leaseExpiry = DateTime.UtcNow + _options.LeaseDuration;

				// Losing the race simply means a peer claimed this schedule.
				if( !await _leaseProvider.TryAcquireAsync( schedule.Identifier, holder, leaseExpiry )
										.ConfigureAwait( false ) )
				{
					_logger.LogDebug(
							"Claimant {Claimant} worker {Worker} lost the lease race for schedule {Schedule} to a peer.",
							claimant, worker, schedule.Identifier );

					continue;
				}

				ClaimedJob job;

				try
				{
					job = await PrepareJobAsync( schedule, claimant, worker, holder, leaseExpiry )
							.ConfigureAwait( false );
				}
				catch( Exception exception )
				{
					_logger.LogError(
							exception,
							"Claimant {Claimant} worker {Worker} failed to prepare a job for schedule {Schedule}; " +
							"releasing its lease.",
							claimant, worker, schedule.Identifier );

					await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );

					throw;
				}

				if( job is not null )
					return job;
			}

			_logger.LogDebug(
					"No due work for claimant {Claimant} worker {Worker} (job type {JobType}).",
					claimant, worker, jobType ?? "any" );

			return null;
		}

		// Turns a claimed schedule into an executable job: resolves the occurrence being fired,
		// classifies the trigger, applies the misfire policy, and records the run. Returns null
		// when the occurrence is disposed of without execution (misfire SKIP or a stale read).
		private async Task<ClaimedJob> PrepareJobAsync( ScheduleInfo schedule, string claimant, int worker, string holder,
														DateTime leaseExpiry )
		{
			ScheduleDefinition definition = schedule.Definition;
			ScheduleState state = schedule.State;

			DateTime now = DateTime.UtcNow;

			// A concurrent completion may have advanced the schedule since the due-candidate
			// read; nothing to execute.
			if( state.NextRun is null || state.NextRun > now )
			{
				_logger.LogWarning(
						"Discarded stale due candidate {Schedule}: it was advanced since the due read.",
						schedule.Identifier );

				await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );

				return null;
			}

			DateTime due = DateTime.SpecifyKind( state.NextRun.Value, DateTimeKind.Utc );

			ScheduleRun latestRun =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetLatestRunAsync( context.schedule ).ConfigureAwait( false ),
							context: new { schedule = schedule.Identifier },
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );

			DateTime scheduledFor;
			string trigger;
			bool skip = false;

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

				skip = misfired && definition.MisfirePolicy.Handling == MisfireHandlings.Skip;
				trigger = misfired ? RunTriggers.Misfire : RunTriggers.Schedule;
			}

			// The candidate was read before the lease was taken. A late completion by the previous
			// holder that landed in between finished the very occurrence and attempt the candidate
			// still names, so recording it again could only collide with that run
			// (uxScheduleRunFire): the candidate is stale. Decided on the finished run itself,
			// not on the schedule's advance, which a provider without atomic units (SQLite) may
			// not have recorded yet.
			if( latestRun is not null && latestRun.Finished is not null &&
				latestRun.ScheduledFor == scheduledFor && latestRun.Attempt == state.Attempt )
			{
				_logger.LogWarning(
						"Discarded stale due candidate {Schedule}: its occurrence {ScheduledFor} (attempt {Attempt}) " +
						"finished as run {Run} since the due read.",
						schedule.Identifier, scheduledFor, state.Attempt, latestRun.Identifier );

				await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );

				return null;
			}

			if( skip )
			{
				await SkipOccurrenceAsync( schedule, holder, due, now ).ConfigureAwait( false );

				return null;
			}

			// Adopt the unfinished run of a lapsed claim (crashed worker) rather than inserting
			// a duplicate record of the same (schedule, occurrence, attempt).
			string runIdentifier;

			if( latestRun is not null && latestRun.Finished is null &&
				latestRun.ScheduledFor == scheduledFor && latestRun.Attempt == state.Attempt )
			{
				runIdentifier = latestRun.Identifier;

				// The claim on the run moves to the adopter; the previous holder's completion is
				// refused from here on. The transfer is conditional on the run still being
				// unfinished: a late completion by the previous holder may have landed since the
				// latest-run read, and a finished run must not be executed again.
				bool adopted = await _applicationDataProvider.ExecuteAsync(
										async ( session, context ) =>
											await session.ClaimRunAsync( context.run, context.holder )
														.ConfigureAwait( false ),
										context: new { run = runIdentifier, holder },
										TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
									).ConfigureAwait( false );

				if( !adopted )
				{
					// Nothing is claimed, so a pending run request is left for the claim that honors
					// it (the completion recording the finish clears any request raised before it).
					_logger.LogWarning(
							"Discarded stale due candidate {Schedule}: its unfinished run {Run} was completed by " +
							"its previous holder before it could be adopted.",
							schedule.Identifier, runIdentifier );

					await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );

					return null;
				}

				_logger.LogInformation(
						"Claimant {Claimant} worker {Worker} adopted unfinished run {Run} of schedule {Schedule} " +
						"(occurrence {ScheduledFor}, attempt {Attempt}).",
						claimant, worker, runIdentifier, schedule.Identifier, scheduledFor, state.Attempt );

				if( state.RunRequested )
					await _applicationDataProvider.PerformAsync(
								async ( session, context ) =>
									await session.ClearRunRequestAsync( context.schedule ).ConfigureAwait( false ),
								context: new { schedule = schedule.Identifier },
								TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
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
					Trigger = trigger,
					ClaimedBy = holder
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
							},
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );
			}

			_logger.LogInformation(
					"Claimant {Claimant} worker {Worker} claimed run {Run} of schedule {Schedule} " +
					"(occurrence {ScheduledFor}, attempt {Attempt}, trigger {Trigger}).",
					claimant, worker, runIdentifier, schedule.Identifier, scheduledFor, state.Attempt, trigger );

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
		private async Task SkipOccurrenceAsync( ScheduleInfo schedule, string holder, DateTime due, DateTime now )
		{
			DateTime? nextRun = RecurrenceCalculator.GetNextOccurrence( schedule.Definition.Recurrence, now );

			_logger.LogInformation(
					"Skipped occurrence {ScheduledFor} of schedule {Schedule} per its misfire policy; next run {NextRun}.",
					due, schedule.Identifier, nextRun );

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
				Result = RunResults.Skipped,
				ClaimedBy = holder
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
						context: new { run, schedule = schedule.Identifier, nextRun, lastRun },
						TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
					).ConfigureAwait( false );

			await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );
		}

		public async Task<bool> RenewClaimAsync( string run, string claimant, int worker )
		{
			string holder = ValidateCaller( claimant, worker );

			if( string.IsNullOrWhiteSpace( run ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(run) );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.RenewClaim, claimant, worker )
				.ConfigureAwait( false );

			ScheduleRun runRecord =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetRunAsync( context.run ).ConfigureAwait( false ),
							context: new { run },
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );

			if( runRecord is null )
			{
				_logger.LogWarning(
						"Refused renewal of unknown run {Run} for claimant {Claimant} worker {Worker}.",
						run, claimant, worker );

				return false;
			}

			bool renewed = await _leaseProvider.RenewAsync(
									runRecord.Schedule, holder, DateTime.UtcNow + _options.LeaseDuration )
								.ConfigureAwait( false );

			if( !renewed )
				_logger.LogWarning(
						"Refused renewal of run {Run} of schedule {Schedule}: claimant {Claimant} worker {Worker} " +
						"no longer holds the claim.",
						run, runRecord.Schedule, claimant, worker );

			return renewed;
		}

		public async Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			string holder = ValidateCaller( claimant, worker );

			if( string.IsNullOrWhiteSpace( run ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(run) );

			// SKIPPED is the coordinator's own classification; workers report executed runs.
			if( result != RunResults.Succeeded && result != RunResults.Failed && result != RunResults.TimedOut )
				throw new ArgumentException( $"Unknown or non-reportable run result '{result}'.", nameof(result) );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.ReportCompletion, claimant, worker )
				.ConfigureAwait( false );

			ScheduleRun runRecord =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetRunAsync( context.run ).ConfigureAwait( false ),
							context: new { run },
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );

			if( runRecord is null )
				throw new UnknownRunException( run );

			ScheduleInfo schedule =
				await _applicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetScheduleAsync( context.schedule ).ConfigureAwait( false ),
							context: new { schedule = runRecord.Schedule },
							TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
						).ConfigureAwait( false );

			if( schedule is null )
				throw new UnknownScheduleException( runRecord.Schedule );

			// A completion is accepted only if the reporter can be verified to hold the run. A
			// finished run is refused outright so a duplicate report cannot complete it or
			// advance the schedule again.
			if( runRecord.Finished is not null )
			{
				_logger.LogWarning(
						"Refused completion of run {Run} of schedule {Schedule} reported by claimant {Claimant} " +
						"worker {Worker}: the run has already finished.",
						run, schedule.Identifier, claimant, worker );

				throw new ClaimNotHeldException( run );
			}

			ExecutionLease lease = await _leaseProvider.GetAsync( schedule.Identifier ).ConfigureAwait( false );
			bool leaseHeld = lease is not null && lease.ClaimedBy == holder;

			if( runRecord.ClaimedBy is not null )
			{
				if( runRecord.ClaimedBy != holder )
				{
					_logger.LogWarning(
							"Refused completion of run {Run} of schedule {Schedule} reported by claimant {Claimant} " +
							"worker {Worker}: the run is recorded as claimed by {Holder}.",
							run, schedule.Identifier, claimant, worker, runRecord.ClaimedBy );

					throw new ClaimNotHeldException( run );
				}

				if( !leaseHeld )
					_logger.LogWarning(
							"Claimant {Claimant} worker {Worker} reported completion of run {Run} of schedule {Schedule} " +
							"after its claim had lapsed (held by {LeaseHolder}); accepted because the run is still " +
							"recorded as its own.",
							claimant, worker, run, schedule.Identifier, lease?.ClaimedBy ?? "nobody" );
			}
			else if( !leaseHeld )
			{
				// A run recorded before the holder was tracked: the current lease is the only evidence.
				_logger.LogWarning(
						"Refused completion of legacy run {Run} of schedule {Schedule} reported by claimant {Claimant} " +
						"worker {Worker}, which does not hold the claim (held by {LeaseHolder}).",
						run, schedule.Identifier, claimant, worker, lease?.ClaimedBy ?? "nobody" );

				throw new ClaimNotHeldException( run );
			}

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

			_logger.LogDebug(
					"Next occurrence of schedule {Schedule} computed: {NextRun} (attempt {Attempt}).",
					schedule.Identifier, nextRun, attempt );

			RunSummary lastRun = new() { Started = runRecord.Started, Finished = now, Result = result };

			// The completing update re-checks the holder, so an adoption that lands after the checks
			// above still refuses this completion rather than overwriting the adopter's run.
			bool completed = await _applicationDataProvider.ExecuteAsync(
									async ( session, context ) =>
									{
										if( !await session.CompleteRunAsync(
													context.run, context.holder, context.now, context.result,
													context.outcome
												)
												.ConfigureAwait( false ) )
											return false;

										await session.RecordOccurrenceCompletionAsync(
														context.schedule, context.attempt, context.nextRun,
														context.lastRun
													)
													.ConfigureAwait( false );

										return true;
									},
									context: new
									{
										run, holder, now, result, outcome, schedule = schedule.Identifier, attempt,
										nextRun, lastRun
									},
									TransactionScopeOption.RequiresNew, ScheduleStoreTransaction
								).ConfigureAwait( false );

			if( !completed )
			{
				_logger.LogWarning(
						"Refused completion of run {Run} of schedule {Schedule} reported by claimant {Claimant} " +
						"worker {Worker}: the run finished or was adopted by another worker while the completion " +
						"was being recorded.",
						run, schedule.Identifier, claimant, worker );

				throw new ClaimNotHeldException( run );
			}

			await _leaseProvider.ReleaseAsync( schedule.Identifier, holder ).ConfigureAwait( false );

			_logger.LogInformation(
					"Recorded completion of run {Run} of schedule {Schedule} by claimant {Claimant} worker {Worker}: " +
					"{Result}.",
					run, schedule.Identifier, claimant, worker, result );

			if( attempt > 0 )
				_logger.LogInformation(
						"Scheduled retry {Attempt} of schedule {Schedule} (occurrence {ScheduledFor}) for {NextRun}.",
						attempt, schedule.Identifier, runRecord.ScheduledFor, nextRun );
		}
	}
}
