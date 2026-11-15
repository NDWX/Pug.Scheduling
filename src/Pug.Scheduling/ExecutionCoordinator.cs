using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Execution-side facade behind which all due-ness, misfire, retry, and next-occurrence
	// policy lives. Workers only see IExecutionCoordinator.
	//
	// Every transition (claim, adopt, skip, renew, complete) is one unit of work that locks the
	// schedule row, decides in C# on what it locked (OccurrenceState, CompletionRules), and writes
	// the decided values. The execution lease lives on that row (active run, holder, expiry), so
	// the lock is what makes a claim exclusive: concurrent coordinators may read overlapping due
	// candidates, and whichever locks a candidate second finds it taken and moves on. The store
	// decides nothing.
	//
	// Authorization lives here rather than in any hosting surface, so it cannot be bypassed by
	// binding the coordinator differently. Every call carrying a security principal requires the
	// corresponding CoordinatorSecurityOperations grant on the claimant identity being acted as,
	// which is what keeps one worker from claiming, renewing, or completing runs as another;
	// calls with no principal at all are in-process by construction and are trusted.
	//
	// Leases are held per worker, as "claimant/worker": grants belong to the group (claimant),
	// exclusivity to the individual worker. A worker restarted under the same claimant and
	// number presents the same holder, so it adopts its predecessor's run even while the lease
	// is live, rather than waiting the lease out.
	public class ExecutionCoordinator : IExecutionCoordinator
	{
		// The worker a call acts as: its group's identity (qualifying grants), its number, and the
		// lease holder they compose.
		private readonly record struct Caller( string Claimant, int Worker, string Holder );

		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IApplicationData<IDataSession> _applicationDataProvider;
		private readonly ISecurityManager _securityManager;
		private readonly ExecutionCoordinatorOptions _options;
		private readonly TimeProvider _timeProvider;
		private readonly ILogger<ExecutionCoordinator> _logger;

		public ExecutionCoordinator( IdentifierGenerator identifierGenerator,
									IApplicationData<IDataSession> applicationDataProvider,
									ISecurityManager securityManager,
									ExecutionCoordinatorOptions options,
									TimeProvider timeProvider,
									ILogger<ExecutionCoordinator> logger )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_applicationDataProvider = applicationDataProvider ??
										throw new ArgumentNullException( nameof(applicationDataProvider) );

			_securityManager = securityManager ?? throw new ArgumentNullException( nameof(securityManager) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );

			if( _options.ClaimCandidates < 1 )
				throw new ArgumentException( "At least one claim candidate is required.", nameof(options) );

			_timeProvider = timeProvider ?? throw new ArgumentNullException( nameof(timeProvider) );

			_logger = logger ?? throw new ArgumentNullException( nameof(logger) );
		}

		private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

		// claimant is the worker group's identity, qualifying its grants; the lease holder is the
		// individual worker within it, composed as "claimant/worker". The separator is reserved
		// so no two (claimant, worker) pairs compose to the same holder.
		private static Caller ValidateCaller( string claimant, int worker )
		{
			if( string.IsNullOrWhiteSpace( claimant ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(claimant) );

			if( claimant.Contains( '/' ) )
				throw new ArgumentException( "Claimant must not contain '/'.", nameof(claimant) );

			if( worker < 1 )
				throw new ArgumentException( "Worker number must be 1 or greater.", nameof(worker) );

			return new Caller( claimant, worker, $"{claimant}/{worker}" );
		}

		private static void ValidateRun( string run )
		{
			if( string.IsNullOrWhiteSpace( run ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(run) );
		}

		// Grants are qualified by the claimant identity being acted as; there is no owner rule
		// here — execution is an operator concern, not the schedule owner's.
		//
		// The absence of a principal identifies an in-process caller and is trusted: every
		// endpoint in front of the coordinator requires an authenticated caller before dispatch,
		// so a call that crossed a transport always arrives with a principal and is always
		// checked. A worker hosted in-process therefore needs no service identity of its own.
		private async Task CheckAuthorizationAsync( string operation, Caller caller )
		{
			if( _securityManager.CurrentPrincipal is null )
				return;

			bool isAuthorized = await _securityManager.CurrentPrincipal
													.IsAuthorizedAsync(
															new Dictionary<string, string>( 0 ),
															operation,
															ResourceQualifiers.ExecutionCoordinator( caller.Claimant ),
															string.Empty
														)
													.ConfigureAwait( false );

			if( isAuthorized )
				return;

			_logger.LogWarning(
					"Authorization denied: {Operation} as claimant {Claimant} (worker {Worker}).",
					operation, caller.Claimant, caller.Worker );

			throw new NotAuthorized();
		}

		// One transition: a write unit in which it locks the schedule row, decides, and writes.
		// An exception rolls the whole unit back.
		private Task<T> InUnitAsync<T>( Func<IDataSession, Task<T>> transition ) =>
			_applicationDataProvider.WriteAsync( ( session, apply ) => apply( session ), transition );

		// A run of the schedule's occurrence started now by the caller.
		private ScheduleRun NewRun( ScheduleInfo schedule, DateTime scheduledFor, int attempt, string trigger,
									Caller caller, DateTime now ) =>
			new()
			{
				Identifier = _identifierGenerator.GetNext(),
				Schedule = schedule.Identifier,
				Domain = schedule.Definition.Domain,
				ScheduledFor = scheduledFor,
				Started = now,
				Attempt = attempt,
				Trigger = trigger,
				ClaimedBy = caller.Holder
			};

		private ExecutionLease NewLease( string run, Caller caller, DateTime now ) =>
			new() { Run = run, ClaimedBy = caller.Holder, Expiry = now + _options.LeaseDuration };

		// == Claim ==

		public async Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType )
		{
			Caller caller = ValidateCaller( claimant, worker );

			if( jobType is not null && string.IsNullOrWhiteSpace( jobType ) )
				throw new ArgumentException( "Value cannot be whitespace; pass null for any job type.", nameof(jobType) );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.ClaimJobs, caller ).ConfigureAwait( false );

			// A small window rather than the single oldest due schedule: losing the race for one
			// candidate to a peer, or failing to claim it, moves on to the next instead of
			// returning nothing while other work is due.
			IEnumerable<string> candidates =
				await _applicationDataProvider.ReadAsync(
							( session, context ) =>
								session.GetDueCandidatesAsync(
										context.now, context.holder, context.jobType, context.maximumSchedules ),
							context: new
							{
								now = UtcNow, holder = caller.Holder, jobType,
								maximumSchedules = _options.ClaimCandidates
							}
						).ConfigureAwait( false );

			Exception lastFailure = null;
			bool anyCandidateHandled = false;

			foreach( string candidate in candidates )
			{
				( ClaimedJob job, Exception failure ) =
					await TryClaimCandidateAsync( candidate, caller ).ConfigureAwait( false );

				if( job is not null )
					return job;

				lastFailure = failure;
				anyCandidateHandled |= failure is null;
			}

			// Every candidate failed: that is a fault, not an idle window, so it is not reported as
			// "no work" — the worker sees the failure.
			if( lastFailure is not null && !anyCandidateHandled )
				ExceptionDispatchInfo.Capture( lastFailure ).Throw();

			_logger.LogDebug(
					"No due work for claimant {Claimant} worker {Worker} (job type {JobType}).",
					caller.Claimant, caller.Worker, jobType ?? "any" );

			return null;
		}

		// A candidate that fails to claim is logged and passed over: one broken schedule must not
		// keep the rest of the window from being claimed. Its unit rolled back, so it stays as it was.
		// The failure is returned so the caller can tell a window that failed throughout from one
		// that merely had nothing to hand out.
		private async Task<(ClaimedJob Job, Exception Failure)> TryClaimCandidateAsync( string schedule, Caller caller )
		{
			try
			{
				return ( await InUnitAsync( session => ClaimAsync( session, schedule, caller ) ).ConfigureAwait( false ), null );
			}
			catch( Exception exception )
			{
				_logger.LogError(
						exception,
						"Claimant {Claimant} worker {Worker} failed to claim schedule {Schedule}; moving to the next candidate.",
						caller.Claimant, caller.Worker, schedule );

				return ( null, exception );
			}
		}

		// Classifies the locked schedule and applies the transition it calls for. Null when there
		// is no job to hand out: the candidate no longer qualifies, or its occurrence was skipped.
		private async Task<ClaimedJob> ClaimAsync( IDataSession session, string identifier, Caller caller )
		{
			ScheduleInfo schedule = await session.GetScheduleAsync( identifier, lockRow: true ).ConfigureAwait( false );

			if( schedule is null )
				return null;

			DateTime now = UtcNow;
			OccurrenceAction action = OccurrenceState.Classify( schedule, now, caller.Holder );

			switch( action )
			{
				case OccurrenceAction.NotDue:
					_logger.LogDebug(
							"Due candidate {Schedule} no longer qualifies for claimant {Claimant} worker {Worker}: " +
							"a peer claimed it, or it was advanced, paused or disabled since the due read.",
							identifier, caller.Claimant, caller.Worker );

					return null;

				case OccurrenceAction.Adopt:
					return await AdoptRunAsync( session, schedule, caller, now ).ConfigureAwait( false );

				case OccurrenceAction.Skip:
					await SkipOccurrenceAsync( session, schedule, caller, now ).ConfigureAwait( false );

					return null;

				default:
					return await StartRunAsync( session, schedule, action, caller, now ).ConfigureAwait( false );
			}
		}

		// Records a new run of the occurrence the action calls for and takes the lease on it. A run
		// request is consumed by the claim that honors it, in the same unit.
		private async Task<ClaimedJob> StartRunAsync( IDataSession session, ScheduleInfo schedule,
													OccurrenceAction action, Caller caller, DateTime now )
		{
			ScheduleRun run = NewRun(
					schedule, OccurrenceState.ScheduledFor( schedule, action, now ), schedule.State.Attempt,
					OccurrenceState.Trigger( action ), caller, now );

			ExecutionLease lease = NewLease( run.Identifier, caller, now );

			await session.InsertRunAsync( run ).ConfigureAwait( false );
			await session.SetExecutionAsync( schedule.Identifier, lease ).ConfigureAwait( false );

			if( action == OccurrenceAction.Manual )
				await session.SetRunRequestedAsync( schedule.Identifier, false ).ConfigureAwait( false );

			_logger.LogInformation(
					"Claimant {Claimant} worker {Worker} claimed run {Run} of schedule {Schedule} " +
					"(occurrence {ScheduledFor}, attempt {Attempt}, trigger {Trigger}).",
					caller.Claimant, caller.Worker, run.Identifier, schedule.Identifier, run.ScheduledFor, run.Attempt,
					run.Trigger );

			return ToClaimedJob( schedule, run, lease );
		}

		// Takes over the schedule's active run (a lapsed claim, or the caller's own after a
		// restart) instead of recording its occurrence again. The run keeps its occurrence,
		// attempt and trigger; the previous holder's renewals and completion are refused from here
		// on.
		private async Task<ClaimedJob> AdoptRunAsync( IDataSession session, ScheduleInfo schedule, Caller caller,
													DateTime now )
		{
			ExecutionLease previous = schedule.ExecutionLease;

			ScheduleRun run = await session.GetRunAsync( previous.Run ).ConfigureAwait( false );

			if( run is null || run.Finished is not null )
			{
				await RepairExecutionAsync( session, schedule, run, now ).ConfigureAwait( false );

				return null;
			}

			ExecutionLease lease = NewLease( run.Identifier, caller, now );

			await session.SetExecutionAsync( schedule.Identifier, lease ).ConfigureAwait( false );
			await session.SetRunClaimantAsync( run.Identifier, caller.Holder ).ConfigureAwait( false );

			_logger.LogInformation(
					"Claimant {Claimant} worker {Worker} adopted run {Run} of schedule {Schedule} from {PreviousHolder} " +
					"(lease expired {PreviousExpiry}; occurrence {ScheduledFor}, attempt {Attempt}).",
					caller.Claimant, caller.Worker, run.Identifier, schedule.Identifier, previous.ClaimedBy,
					previous.Expiry, run.ScheduledFor, run.Attempt );

			return ToClaimedJob( schedule, run with { ClaimedBy = caller.Holder }, lease );
		}

		// Every transition that finishes a run clears it from the schedule in the same unit, so a
		// schedule naming a missing or finished run as in progress is a damaged row. It is cleared so
		// the schedule can be claimed again, and a finished run's completion is applied as its
		// completion would have, so its occurrence is not recorded a second time.
		private async Task RepairExecutionAsync( IDataSession session, ScheduleInfo schedule, ScheduleRun run,
												DateTime now )
		{
			_logger.LogError(
					"Schedule {Schedule} names run {Run} as in progress, but the run is {RunState}; cleared it so the " +
					"schedule can be claimed again.",
					schedule.Identifier, schedule.ExecutionLease.Run, run is null ? "missing" : "finished" );

			if( run is null )
				await session.SetExecutionAsync( schedule.Identifier, null ).ConfigureAwait( false );
			else
				await ReleaseAndProgressAsync(
								session, schedule, CompletionRules.Next( schedule, run, run.Result, now, run.Finished ) )
							.ConfigureAwait( false );
		}

		// Misfire SKIP: disposes of the missed occurrence without execution — a SKIPPED run for
		// the audit trail, and the schedule advanced past it.
		private async Task SkipOccurrenceAsync( IDataSession session, ScheduleInfo schedule, Caller caller,
												DateTime now )
		{
			DateTime occurrence = OccurrenceState.ScheduledFor( schedule, OccurrenceAction.Skip, now );
			OccurrenceProgress progress = CompletionRules.Skipped( schedule, occurrence, now );

			ScheduleRun run = NewRun(
							schedule, occurrence, 0, OccurrenceState.Trigger( OccurrenceAction.Skip ), caller, now )
						with { Finished = now, Result = RunResults.Skipped };

			await session.InsertRunAsync( run ).ConfigureAwait( false );
			await session.SetOccurrenceStateAsync( schedule.Identifier, progress ).ConfigureAwait( false );

			_logger.LogInformation(
					"Skipped occurrence {ScheduledFor} of schedule {Schedule} per its misfire policy; next run {NextRun}.",
					occurrence, schedule.Identifier, progress.NextRun );
		}

		private static ClaimedJob ToClaimedJob( ScheduleInfo schedule, ScheduleRun run, ExecutionLease lease ) =>
			new()
			{
				Context = new ScheduledJobContext
				{
					Schedule = schedule.Identifier,
					Run = run.Identifier,
					Domain = schedule.Definition.Domain,
					Owner = schedule.Owner,
					JobType = schedule.Definition.JobType,
					Payload = schedule.Definition.Payload,
					ScheduledFor = run.ScheduledFor,
					Attempt = run.Attempt,
					Trigger = run.Trigger
				},
				Timeout = schedule.Definition.Timeout,
				ClaimExpiry = lease.Expiry
			};

		// == Renew ==

		public async Task<bool> RenewClaimAsync( string run, string claimant, int worker )
		{
			Caller caller = ValidateCaller( claimant, worker );

			ValidateRun( run );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.RenewClaim, caller ).ConfigureAwait( false );

			return await InUnitAsync( session => RenewAsync( session, run, caller ) ).ConfigureAwait( false );
		}

		// Extends the lease while the schedule still names this run and this holder. Expiry is not
		// checked: a lapsed lease nobody adopted is still the holder's own (as a restarted holder
		// re-takes its own live one). False tells the worker to abandon the run.
		private Task<bool> RenewAsync( IDataSession session, string run, Caller caller ) =>
			SetHeldLeaseExpiryAsync( session, run, caller, "renewal", UtcNow + _options.LeaseDuration );

		// == Abandon ==

		public async Task<bool> AbandonClaimAsync( string run, string claimant, int worker )
		{
			Caller caller = ValidateCaller( claimant, worker );

			ValidateRun( run );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.AbandonClaim, caller ).ConfigureAwait( false );

			bool abandoned = await InUnitAsync( session => AbandonAsync( session, run, caller ) ).ConfigureAwait( false );

			if( abandoned )
				_logger.LogInformation(
						"Claimant {Claimant} worker {Worker} abandoned run {Run}; a peer may adopt it at its next claim.",
						caller.Claimant, caller.Worker, run );

			return abandoned;
		}

		// Expires the lease now, leaving the run and its holder in place: the next claim by any
		// worker classifies the run as an adoption (D9), and the holder's own late completion is
		// still accepted until then (a lapsed claim nobody adopted is the holder's own).
		private Task<bool> AbandonAsync( IDataSession session, string run, Caller caller ) =>
			SetHeldLeaseExpiryAsync( session, run, caller, "abandonment", UtcNow );

		// Locks the schedule whose active run is run and, only if the caller holds it, rewrites
		// the lease with the given expiry. Expiry is not checked: a lapsed lease nobody adopted is
		// still the holder's own (as a restarted holder re-takes its own live one). False means
		// the claim is not held.
		private async Task<bool> SetHeldLeaseExpiryAsync( IDataSession session, string run, Caller caller,
														string action, DateTime expiry )
		{
			ScheduleInfo schedule = await session.GetScheduleByActiveRunAsync( run, lockRow: true ).ConfigureAwait( false );

			ExecutionLease lease = schedule?.ExecutionLease;

			if( lease?.ClaimedBy != caller.Holder )
			{
				_logger.LogWarning(
						"Refused {Action} of run {Run}: claimant {Claimant} worker {Worker} no longer holds it " +
						"(the run is {RunState}).",
						action, run, caller.Claimant, caller.Worker,
						lease is null ? "not in progress" : $"held by {lease.ClaimedBy}" );

				return false;
			}

			await session.SetExecutionAsync( schedule.Identifier, lease with { Expiry = expiry } ).ConfigureAwait( false );

			return true;
		}

		// == Complete ==

		public async Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome )
		{
			Caller caller = ValidateCaller( claimant, worker );

			ValidateRun( run );

			// SKIPPED is the coordinator's own classification; workers report executed runs.
			if( result != RunResults.Succeeded && result != RunResults.Failed && result != RunResults.TimedOut )
				throw new ArgumentException( $"Unknown or non-reportable run result '{result}'.", nameof(result) );

			await CheckAuthorizationAsync( CoordinatorSecurityOperations.ReportCompletion, caller )
				.ConfigureAwait( false );

			(string schedule, ScheduleRun completed, OccurrenceProgress progress) =
				await InUnitAsync( session => CompleteAsync( session, run, caller, result, outcome ) )
					.ConfigureAwait( false );

			_logger.LogInformation(
					"Recorded completion of run {Run} of schedule {Schedule} by claimant {Claimant} worker {Worker}: " +
					"{Result}; next run {NextRun} (attempt {Attempt}).",
					run, schedule, caller.Claimant, caller.Worker, result, progress.NextRun, progress.Attempt );

			if( progress.Attempt > 0 )
				_logger.LogInformation(
						"Scheduled retry {Attempt} of schedule {Schedule} (occurrence {ScheduledFor}) for {NextRun}.",
						progress.Attempt, schedule, completed.ScheduledFor, progress.NextRun );
		}

		// Resolves the run being completed and records its completion; returns what the caller logs.
		private async Task<(string Schedule, ScheduleRun Run, OccurrenceProgress Progress)> CompleteAsync(
			IDataSession session, string run, Caller caller, string result, JobOutcome outcome )
		{
			(ScheduleInfo locked, ScheduleRun record) =
				await ResolveCompletionAsync( session, run, caller ).ConfigureAwait( false );

			OccurrenceProgress next =
				await RecordCompletionAsync( session, locked, record, result, outcome ).ConfigureAwait( false );

			return ( locked.Identifier, record, next );
		}

		// The locked schedule and the run being completed, if the reporter holds the run. The run
		// must exist (UnknownRunException otherwise, as for a schedule deleted mid-run), be the
		// schedule's active run, and be held by the reporter (ClaimNotHeldException otherwise:
		// already finished, or adopted by a peer). A lapsed claim nobody adopted is still the
		// reporter's own and is accepted.
		private async Task<(ScheduleInfo, ScheduleRun)> ResolveCompletionAsync( IDataSession session, string run,
																				Caller caller )
		{
			ScheduleInfo schedule = await session.GetScheduleByActiveRunAsync( run, lockRow: true ).ConfigureAwait( false );

			ScheduleRun record = await session.GetRunAsync( run ).ConfigureAwait( false );

			if( record is null )
				throw new UnknownRunException( run );

			ExecutionLease lease = schedule?.ExecutionLease;

			if( lease is null || lease.ClaimedBy != caller.Holder || record.Finished is not null )
			{
				_logger.LogWarning(
						"Refused completion of run {Run} of schedule {Schedule} reported by claimant {Claimant} " +
						"worker {Worker}: the run is {RunState}.",
						run, record.Schedule, caller.Claimant, caller.Worker,
						lease is null || record.Finished is not null ? "not in progress" : $"held by {lease.ClaimedBy}" );

				throw new ClaimNotHeldException( run );
			}

			if( lease.Expiry <= UtcNow )
				_logger.LogWarning(
						"Claimant {Claimant} worker {Worker} reported completion of run {Run} of schedule {Schedule} " +
						"after its claim had lapsed; accepted because no peer adopted the run.",
						caller.Claimant, caller.Worker, run, schedule.Identifier );

			return ( schedule, record );
		}

		// Finishes the run, releases the schedule's lease, and progresses its occurrence, all in
		// the completing unit.
		private async Task<OccurrenceProgress> RecordCompletionAsync( IDataSession session, ScheduleInfo schedule,
																	ScheduleRun run, string result, JobOutcome outcome )
		{
			DateTime now = UtcNow;
			OccurrenceProgress progress = CompletionRules.Next( schedule, run, result, now );

			await session.FinishRunAsync( run.Identifier, now, result, outcome ).ConfigureAwait( false );
			await ReleaseAndProgressAsync( session, schedule, progress ).ConfigureAwait( false );

			return progress;
		}

		// Releases the schedule's lease and writes its progressed occurrence.
		private static async Task ReleaseAndProgressAsync( IDataSession session, ScheduleInfo schedule,
															OccurrenceProgress progress )
		{
			await session.SetExecutionAsync( schedule.Identifier, null ).ConfigureAwait( false );
			await session.SetOccurrenceStateAsync( schedule.Identifier, progress ).ConfigureAwait( false );
		}
	}
}
