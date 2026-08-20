using Pug.Application.Data;
using Pug.Application.Data.Extensions;
using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling
{
	// Management facade over the schedule store: configuration and observation only — never
	// execution. Enforces validation, service-level defaults, and authorization (owner rule
	// before role-based ACLs); composes ExecutionLease from the lease provider at read time.
	public class Scheduler : SchedulerBase, IScheduler
	{
		private readonly IdentifierGenerator _identifierGenerator;
		private readonly IExecutionLeaseProvider _leaseProvider;
		private readonly IJobTypeRegistry _jobTypeRegistry;
		private readonly SchedulerOptions _options;

		public Scheduler( IdentifierGenerator identifierGenerator,
						IApplicationData<IDataSession> applicationDataProvider,
						ISecurityManager securityManager,
						IExecutionLeaseProvider leaseProvider,
						IJobTypeRegistry jobTypeRegistry,
						SchedulerOptions options )
			: base( applicationDataProvider, securityManager )
		{
			_identifierGenerator = identifierGenerator ??
									throw new ArgumentNullException( nameof(identifierGenerator) );

			_leaseProvider = leaseProvider ?? throw new ArgumentNullException( nameof(leaseProvider) );

			_jobTypeRegistry = jobTypeRegistry ?? throw new ArgumentNullException( nameof(jobTypeRegistry) );

			_options = options ?? throw new ArgumentNullException( nameof(options) );
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
				await ApplicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetScheduleAsync( context.identifier ).ConfigureAwait( false ),
							context: new { identifier }
						).ConfigureAwait( false );

			if( schedule == null )
				throw new UnknownScheduleException( identifier );

			return schedule;
		}

		private async Task<ScheduleInfo> ComposeLeaseAsync( ScheduleInfo schedule ) =>
			schedule with
			{
				ExecutionLease = await _leaseProvider.GetAsync( schedule.Identifier ).ConfigureAwait( false )
			};

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
					NextRun = RecurrenceCalculator.GetNextOccurrence( definition.Recurrence, DateTime.UtcNow ),
					Attempt = 0
				},
				Registration = CreateActionContext()
			};

			await ApplicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.InsertAsync( context.schedule ).ConfigureAwait( false ),
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

			IEnumerable<ScheduleInfo> schedules =
				await ApplicationDataProvider.ExecuteAsync(
							async ( session, context ) =>
								await session.GetSchedulesAsync( context.domain, context.owner, context.jobType )
											.ConfigureAwait( false ),
							context: new { domain, owner, jobType }
						).ConfigureAwait( false );

			List<ScheduleInfo> composed = new();

			foreach( ScheduleInfo schedule in schedules )
				composed.Add( await ComposeLeaseAsync( schedule ).ConfigureAwait( false ) );

			return composed;
		}

		public async Task<ScheduleInfo> GetScheduleAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo schedule = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.GetInfo, schedule ).ConfigureAwait( false );

			return await ComposeLeaseAsync( schedule ).ConfigureAwait( false );
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

			// A changed recurrence restarts occurrence computation from now; otherwise the
			// schedule keeps its current next occurrence.
			DateTime? nextRun = definition.Recurrence == existing.Definition.Recurrence
									? existing.State.NextRun
									: RecurrenceCalculator.GetNextOccurrence( definition.Recurrence, DateTime.UtcNow );

			await ApplicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.UpdateDefinitionAsync(
											context.identifier, context.definition, context.nextRun,
											context.lastUpdate
										)
										.ConfigureAwait( false ),
						context: new { identifier, definition, nextRun, lastUpdate = CreateActionContext() }
					).ConfigureAwait( false );
		}

		public async Task DeleteAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.Delete, existing ).ConfigureAwait( false );

			await ApplicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.DeleteAsync( context.identifier ).ConfigureAwait( false ),
						context: new { identifier }
					).ConfigureAwait( false );
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

			await ApplicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.SetPausedAsync( context.identifier, context.paused )
										.ConfigureAwait( false ),
						context: new { identifier, paused }
					).ConfigureAwait( false );
		}

		public async Task RunNowAsync( string identifier )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.RunNow, existing ).ConfigureAwait( false );

			// Raises the RunRequested marker with the moved occurrence, so the coordinator
			// classifies (and records) the resulting run as MANUAL.
			await ApplicationDataProvider.PerformAsync(
						async ( session, context ) =>
							await session.RequestRunAsync( context.identifier, context.requestedTime )
										.ConfigureAwait( false ),
						context: new { identifier, requestedTime = DateTime.UtcNow }
					).ConfigureAwait( false );
		}

		public async Task<IEnumerable<ScheduleRun>> GetRunsAsync( string identifier, DateTime? from = null,
																DateTime? to = null )
		{
			ValidateIdentifier( identifier );

			ScheduleInfo existing = await GetExistingAsync( identifier ).ConfigureAwait( false );

			await CheckScheduleAuthorizationAsync( SecurityOperations.GetRunHistory, existing )
				.ConfigureAwait( false );

			return await ApplicationDataProvider.ExecuteAsync(
						async ( session, context ) =>
							await session.GetRunsAsync( context.identifier, context.from, context.to )
										.ConfigureAwait( false ),
						context: new { identifier, from, to }
					).ConfigureAwait( false );
		}
	}
}
