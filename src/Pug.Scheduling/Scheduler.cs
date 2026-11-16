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
		// (InLockedUnitAsync), the coordinator's lock-decide-write shape. A unit that needs the time
		// reads it from the clock inside the unit, after its lock (ISchedulingClock); run-now, pause
		// and delete decide nothing by time.

		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IJobTypeRegistry _jobTypeRegistry;
		private readonly SchedulerOptions _options;

		public Scheduler( IdentifierGenerator identifierGenerator,
						IApplicationData<IDataSession> applicationDataProvider,
						ISecurityManager securityManager,
						IJobTypeRegistry jobTypeRegistry,
						SchedulerOptions options,
						ISchedulingClock clock )
			: base( applicationDataProvider, securityManager, clock )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_jobTypeRegistry = jobTypeRegistry ?? throw new ArgumentNullException( nameof(jobTypeRegistry) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );

			// The host's own limits and defaults are validated once, here, so a default can never
			// introduce a sub-second or too-small value into a definition.
			DurationRules.RequireWholeSeconds(
					_options.MinimumMisfireThreshold, nameof(SchedulerOptions.MinimumMisfireThreshold), nameof(options) );

			if( _options.MinimumMisfireThreshold < TimeSpan.Zero )
				throw new ArgumentException(
						$"{nameof(SchedulerOptions.MinimumMisfireThreshold)} must not be negative.", nameof(options) );

			if( _options.DefaultMisfirePolicy is not null )
				ValidateThreshold(
						_options.DefaultMisfirePolicy.Threshold, nameof(SchedulerOptions.DefaultMisfirePolicy), nameof(options) );

			if( _options.DefaultRetryPolicy is not null )
				DurationRules.RequireWholeSeconds(
						_options.DefaultRetryPolicy.Backoff, "DefaultRetryPolicy backoff", nameof(options) );

			DurationRules.RequireWholeSeconds(
					_options.DefaultTimeout, nameof(SchedulerOptions.DefaultTimeout), nameof(options) );

			if( _options.DefaultTimeout < DurationRules.MinimumTimeout )
				throw new ArgumentException(
						$"{nameof(SchedulerOptions.DefaultTimeout)} must be at least {DurationRules.MinimumTimeout}.",
						nameof(options) );

			ValidateTimeoutBound( _options.DefaultTimeout, nameof(SchedulerOptions.DefaultTimeout), nameof(options) );
		}

		// A misfire threshold: whole seconds and at least the host's minimum (which is not negative).
		private void ValidateThreshold( TimeSpan threshold, string what, string parameter )
		{
			DurationRules.RequireWholeSeconds( threshold, what + " threshold", parameter );

			if( threshold < _options.MinimumMisfireThreshold )
				throw new ArgumentException(
						$"{what} threshold must be at least {_options.MinimumMisfireThreshold} " +
						$"({nameof(SchedulerOptions.MinimumMisfireThreshold)}).", parameter );
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

				ValidateThreshold( definition.MisfirePolicy.Threshold, "Misfire", nameof(definition) );
			}

			if( definition.RetryPolicy is not null )
			{
				if( definition.RetryPolicy.MaximumAttempts < 1 )
					throw new ArgumentException( "Retry policy must allow at least one attempt.", nameof(definition) );

				DurationRules.RequireWholeSeconds( definition.RetryPolicy.Backoff, "Retry backoff", nameof(definition) );

				if( definition.RetryPolicy.Backoff < TimeSpan.Zero )
					throw new ArgumentException( "Retry backoff must not be negative.", nameof(definition) );
			}

			// Zero means "omitted": ApplyDefaults substitutes DefaultTimeout (itself at least
			// MinimumTimeout), so a stored timeout is always at least MinimumTimeout. A specified
			// timeout must be whole seconds and at least MinimumTimeout.
			DurationRules.RequireWholeSeconds( definition.Timeout, "Timeout", nameof(definition) );

			if( definition.Timeout < TimeSpan.Zero )
				throw new ArgumentException( "Timeout must not be negative.", nameof(definition) );

			if( definition.Timeout != TimeSpan.Zero && definition.Timeout < DurationRules.MinimumTimeout )
				throw new ArgumentException(
						$"Timeout, when specified, must be at least {DurationRules.MinimumTimeout}.", nameof(definition) );

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
		// schedule may have been deleted since it was authorized: UnknownScheduleException then. The
		// lock waits for a concurrent holder (a claim or completion of the schedule) rather than
		// skipping: a held row would read as deleted.
		private Task InLockedUnitAsync( string identifier, Func<IDataSession, ScheduleInfo, Task> write ) =>
			ApplicationDataProvider.WriteAsync(
					async ( session, context ) =>
					{
						ScheduleInfo locked =
							await session.GetScheduleAsync( context.identifier, RowLock.Wait ).ConfigureAwait( false ) ??
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
			// grant; registering on behalf of another principal requires a CREATE_ON_BEHALF grant
			// on that principal in the schedule's domain.
			if( !IsCurrentUser( owner ) )
				await CheckAuthorizationAsync(
							SecurityOperations.CreateOnBehalf, ResourceQualifiers.Principal( definition.Domain, owner ) )
					.ConfigureAwait( false );

			string identifier = _identifierGenerator.GetNext();

			// The first occurrence and the registration stamp are taken at the clock's time in the
			// inserting unit.
			await ApplicationDataProvider.WriteAsync(
						async ( session, context ) =>
						{
							DateTime now = await Clock.GetUtcNowAsync( session ).ConfigureAwait( false );

							await session.InsertAsync(
											new ScheduleInfo
											{
												Identifier = context.identifier,
												Definition = context.definition,
												Owner = context.owner,
												State = new ScheduleState
												{
													Enabled = true,
													Paused = false,
													NextRun = RecurrenceCalculator.GetFirstOccurrence(
															context.definition.Recurrence, now ),
													Attempt = 0
												},
												Registration = CreateActionContext( now )
											} )
										.ConfigureAwait( false );
						},
						context: new { identifier, definition, owner }
					).ConfigureAwait( false );

			return identifier;
		}

		public async Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference owner = null,
																		string jobType = null )
		{
			if( string.IsNullOrWhiteSpace( domain ) )
				throw new ArgumentException( "Value cannot be null or whitespace.", nameof(domain) );

			// A given owner is a full principal: the store matches its type and its identifier, so
			// a missing half would silently match nothing.
			if( owner is not null && ( string.IsNullOrWhiteSpace( owner.Type ) ||
										string.IsNullOrWhiteSpace( owner.Identifier ) ) )
				throw new ArgumentException( "Owner type and identifier cannot be null or whitespace.", nameof(owner) );

			// Listing one's own schedules requires no ACL grant; any broader listing does. "Own"
			// is the owner rule (a USER principal with the caller's identifier): a principal of
			// another type that shares the identifier is someone else's.
			if( !IsCurrentUser( owner ) )
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

			// The next occurrence is decided on the locked row, at the clock's time once it is locked,
			// so a completion committed since the read above is never reverted. A run in progress, a
			// pending retry and a run request are left as they are.
			await InLockedUnitAsync(
						identifier,
						async ( session, locked ) =>
						{
							DateTime now = await Clock.GetUtcNowAsync( session ).ConfigureAwait( false );

							await session.UpdateDefinitionAsync(
											identifier, definition, DefinitionRules.NextRunAfterUpdate( locked, definition, now ),
											CreateActionContext( now ) )
										.ConfigureAwait( false );
						}
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
