using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Management facade over the schedule store: configuration and observation only — never
	// execution. Enforces validation, service-level defaults, and authorization (owner rule
	// before role-based ACLs).
	public class Scheduler : SchedulerBase, IScheduler
	{
		// Every schedule-store unit is a UnitsOfWork read or write of its own, never part of a
		// caller's transaction. Authorization is decided on a read of the schedule (its owner and
		// domain never change); a write that depends on the schedule's state — run-now, a
		// definition update, a delete — locks the row in its own unit and decides on what it locked
		// (InLockedUnitAsync), the coordinator's lock-decide-write shape.

		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IJobTypeRegistry _jobTypeRegistry;
		private readonly SchedulerOptions _options;

		public Scheduler( IdentifierGenerator identifierGenerator,
						IApplicationData<IDataSession> applicationDataProvider,
						ISecurityManager securityManager,
						IJobTypeRegistry jobTypeRegistry,
						SchedulerOptions options,
						TimeProvider timeProvider )
			: base( applicationDataProvider, securityManager, timeProvider )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_jobTypeRegistry = jobTypeRegistry ?? throw new ArgumentNullException( nameof(jobTypeRegistry) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );

			ValidateTimeoutBound( _options.DefaultTimeout, nameof(SchedulerOptions.DefaultTimeout), nameof(options) );
		}

		private static void ValidateIdentifier( string identifier )
		{
			if( string.IsNullOrWhiteSpace( identifier ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(identifier) );
		}

		private void ValidateDefinition( ScheduleDefinition definition )
		{
			if( definition == null ) throw new ArgumentNullException( nameof(definition) );

			if( string.IsNullOrWhiteSpace( definition.Domain ) )
				throw new ArgumentException( "Schedule domain is required.", nameof(definition) );

			if( string.IsNullOrWhiteSpace( definition.Name ) )
				throw new ArgumentException( "Schedule name is required.", nameof(definition) );

			if( string.IsNullOrWhiteSpace( definition.JobType ) )
				throw new ArgumentException( "Schedule job type is required.", nameof(definition) );

			if( !_jobTypeRegistry.IsRegistered( definition.JobType ) )
				throw new ArgumentException(
						$"Job type '{definition.JobType}' is not registered.", nameof(definition) );

			RecurrenceCalculator.Validate( definition.Recurrence );

			if( definition.MisfirePolicy is not null )
			{
				if( definition.MisfirePolicy.Handling != MisfireHandlings.FireOnce &&
					definition.MisfirePolicy.Handling != MisfireHandlings.Skip &&
					definition.MisfirePolicy.Handling != MisfireHandlings.RunAll )
					throw new ArgumentException(
							$"Unknown misfire handling '{definition.MisfirePolicy.Handling}'.", nameof(definition) );

				if( definition.MisfirePolicy.Threshold < TimeSpan.Zero )
					throw new ArgumentException( "Misfire threshold must not be negative.", nameof(definition) );
			}

			if( definition.RetryPolicy is not null )
			{
				if( definition.RetryPolicy.MaximumAttempts < 1 )
					throw new ArgumentException( "Retry policy must allow at least one attempt.", nameof(definition) );

				if( definition.RetryPolicy.Backoff < TimeSpan.Zero )
					throw new ArgumentException( "Retry backoff must not be negative.", nameof(definition) );
			}

			if( definition.Timeout < TimeSpan.Zero )
				throw new ArgumentException( "Timeout must not be negative.", nameof(definition) );

			ValidateTimeoutBound( definition.Timeout, nameof(definition.Timeout), nameof(definition) );
		}

		private static void ValidateTimeoutBound( TimeSpan timeout, string what, string parameter )
		{
			if( timeout > TimerBounds.Maximum )
				throw new ArgumentException(
						$"{what} must not exceed {TimerBounds.Maximum} (about 49.7 days).", parameter );
		}

		// Registrations that omit optional policies receive the service-level defaults.
		private ScheduleDefinition ApplyDefaults( ScheduleDefinition definition ) =>
			definition with
			{
				MisfirePolicy = definition.MisfirePolicy ?? _options.DefaultMisfirePolicy,
				RetryPolicy = definition.RetryPolicy ?? _options.DefaultRetryPolicy,
				Timeout = definition.Timeout <= TimeSpan.Zero ? _options.DefaultTimeout : definition.Timeout
			};

		private async Task ValidatePayloadAsync( ScheduleDefinition definition )
		{
			IJobPayloadValidator validator = _jobTypeRegistry.GetPayloadValidator( definition.JobType );

			if( validator == null )
				return;

			List<string> errors = ( await validator.ValidateAsync( definition ).ConfigureAwait( false ) )
								?.ToList() ?? new List<string>( 0 );

			if( errors.Count > 0 )
				throw new ArgumentException(
						$"Schedule payload is not valid: {string.Join( "; ", errors )}", nameof(definition) );
		}

		private async Task<ScheduleInfo> GetExistingAsync( string identifier )
		{
			ScheduleInfo schedule =
				await ApplicationDataProvider.ReadAsync(
							( session, context ) => session.GetScheduleAsync( context.identifier ),
							context: new { identifier }
						).ConfigureAwait( false );

			if( schedule == null )
				throw new UnknownScheduleException( identifier );

			return schedule;
		}

		// A write unit that locks the schedule row and applies write to what it locked. The
		// schedule may have been deleted since it was authorized: UnknownScheduleException then.
		private Task InLockedUnitAsync( string identifier, Func<IDataSession, ScheduleInfo, Task> write ) =>
			ApplicationDataProvider.WriteAsync(
					async ( session, context ) =>
					{
						ScheduleInfo locked =
							await session.GetScheduleAsync( context.identifier, lockRow: true ).ConfigureAwait( false ) ??
							throw new UnknownScheduleException( context.identifier );

						await context.write( session, locked ).ConfigureAwait( false );
					},
					context: new { identifier, write }
				);

		public async Task<string> RegisterAsync( ScheduleDefinition definition, Reference owner = null )
		{
			ValidateDefinition( definition );

			definition = ApplyDefaults( definition );

			await ValidatePayloadAsync( definition ).ConfigureAwait( false );

			// The registering principal owns the schedule unless another owner is specified.
			owner ??= new Reference { Type = "USER", Identifier = CurrentUserIdentifier };

			if( string.IsNullOrWhiteSpace( owner.Identifier ) )
				throw new ArgumentException( "Owner principal identifier is required.", nameof(owner) );

			// Owner rule extends to creation: registering a schedule for oneself requires no ACL
			// grant; registering on behalf of another principal does.
			if( !IsCurrentUser( owner ) )
				await CheckAuthorizationAsync(
							SecurityOperations.Create, ResourceQualifiers.AnySchedule( definition.Domain ) )
					.ConfigureAwait( false );

			ScheduleInfo schedule = new()
			{
				Identifier = _identifierGenerator.GetNext(),
				Definition = definition,
				Owner = owner,
				State = new ScheduleState
				{
					Enabled = true,
					Paused = false,
					NextRun = RecurrenceCalculator.GetFirstOccurrence( definition.Recurrence, UtcNow ),
					Attempt = 0
				},
				Registration = CreateActionContext()
			};

			await ApplicationDataProvider.WriteAsync(
						( session, context ) => session.InsertAsync( context.schedule ),
						context: new { schedule }
					).ConfigureAwait( false );

			return schedule.Identifier;
		}

		public async Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference owner = null,
																		string jobType = null )
		{
			if( string.IsNullOrWhiteSpace( domain ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(domain) );

			// Listing one's own schedules requires no ACL grant; any broader listing does.
			if( owner?.Identifier != CurrentUserIdentifier )
				await CheckAuthorizationAsync( SecurityOperations.List, ResourceQualifiers.AnySchedule( domain ) )
					.ConfigureAwait( false );

			return await ApplicationDataProvider.ReadAsync(
						( session, context ) => session.GetSchedulesAsync( context.domain, context.owner, context.jobType ),
						context: new { domain, owner, jobType }
					).ConfigureAwait( false );
		}

		public async Task<ScheduleInfo> GetScheduleAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo schedule = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.GetInfo, schedule ).ConfigureAwait( false );

			return schedule;
		}

		public async Task UpdateDefinitionAsync( string identifier, ScheduleDefinition definition )
		{
			ValidateIdentifier( identifier );
			ValidateDefinition( definition );

			definition = ApplyDefaults( definition );

			await ValidatePayloadAsync( definition ).ConfigureAwait( false );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.SetDefinition, existing )
				.ConfigureAwait( false );

			// Domain is immutable: it anchors tenancy isolation. (Owner is not part of the
			// definition and can never change.)
			if( definition.Domain != existing.Definition.Domain )
				throw new ArgumentException( "Schedule domain cannot be changed.", nameof(definition) );

			// The next occurrence is decided on the locked row, so a completion committed since the
			// read above is never reverted. A run in progress, a pending retry and a run request are
			// left as they are.
			await InLockedUnitAsync(
						identifier,
						( session, locked ) => session.UpdateDefinitionAsync(
								identifier, definition, DefinitionRules.NextRunAfterUpdate( locked, definition, UtcNow ),
								CreateActionContext() )
					).ConfigureAwait( false );
		}

		public async Task DeleteAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.Delete, existing ).ConfigureAwait( false );

			// Locking the schedule first takes its row before its runs, the order every coordinator
			// transition takes them in (Postgres would otherwise deadlock a delete against a
			// completion). A run in progress is not interrupted: its worker's next renewal fails, and
			// its completion is refused as an unknown run.
			await InLockedUnitAsync( identifier, ( session, _ ) => session.DeleteAsync( identifier ) )
				.ConfigureAwait( false );
		}

		public Task PauseAsync( string identifier ) => SetPausedAsync( identifier, true, SecurityOperations.Pause );

		// Resuming only lifts the pause: occurrences elapsed while paused are evaluated against
		// the schedule's misfire policy by the execution coordinator at its next claim.
		public Task ResumeAsync( string identifier ) => SetPausedAsync( identifier, false, SecurityOperations.Resume );

		private async Task SetPausedAsync( string identifier, bool paused, string operation )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( operation, existing ).ConfigureAwait( false );

			await ApplicationDataProvider.WriteAsync(
						( session, context ) => session.SetPausedAsync( context.identifier, context.paused ),
						context: new { identifier, paused }
					).ConfigureAwait( false );
		}

		public async Task RunNowAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.RunNow, existing ).ConfigureAwait( false );

			// Raises the RunRequested marker, which the coordinator honors with a MANUAL run. A run
			// in progress, live or lapsed, refuses the request: the next occurrence never moves.
			await InLockedUnitAsync(
						identifier,
						( session, locked ) =>
						{
							ExecutionLease lease = locked.ExecutionLease;

							if( lease is not null )
								throw new RunInProgressException( identifier, lease.Run, lease.ClaimedBy, lease.Expiry );

							return session.SetRunRequestedAsync( identifier, true );
						}
					).ConfigureAwait( false );
		}

		public async Task<IEnumerable<ScheduleRun>> GetRunsAsync( string identifier, DateTime? from = null,
																DateTime? to = null )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.GetRunHistory, existing )
				.ConfigureAwait( false );

			return await ApplicationDataProvider.ReadAsync(
						( session, context ) => session.GetRunsAsync( context.identifier, context.from, context.to ),
						context: new { identifier, from, to }
					).ConfigureAwait( false );
		}
	}
}
