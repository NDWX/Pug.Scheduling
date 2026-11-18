using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pug.Application.Security;
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.WebApi;

public static class SchedulingEndpoints
{
	/// <summary>
	/// Maps schedule management API endpoints, under <see cref="SchedulingEndpointOptions.BasePath"/>
	/// (default <c>schedules</c>), onto the given route builder: the host's routes are not assumed
	/// to be the scheduler's alone, and the scheduler host serves the execution coordinator's
	/// routes alongside these. Pass
	/// <c>app</c> to mount at the application root, or a <see cref="RouteGroupBuilder"/> from
	/// <c>app.MapGroup("/your/prefix")</c> to mount the whole surface beneath a further prefix.
	/// All endpoints require an authenticated caller whom the host's <see cref="ISecurityManager"/>
	/// maps to a principal, plus the policies of
	/// <see cref="SchedulingEndpointOptions.AuthorizationPolicies"/> (the host's <c>DefaultPolicy</c>
	/// when it sets none); neither a host policy nor <c>AllowAnonymous()</c> on the returned group
	/// lifts that (401 without
	/// authentication, 403 without a principal). Authorization (the schedule owner rule followed by
	/// role-based ACLs) is enforced by <see cref="IScheduler"/> itself, so these endpoints only
	/// translate its outcomes into HTTP results.
	/// Endpoints for externally hosted workers are mapped separately by
	/// <see cref="ExecutionCoordinatorEndpoints.MapExecutionCoordinatorEndpoints"/>; worker
	/// management endpoints live in <c>Pug.Scheduling.Worker.WebApi</c>, so a worker host need
	/// not reference this package at all.
	/// Swagger documentation is surfaced when the host configures <c>AddEndpointsApiExplorer()</c>
	/// and <c>AddSwaggerGen()</c>.
	/// Returns the endpoints' route group so the host can add further conventions to them.
	/// </summary>
	public static RouteGroupBuilder MapSchedulingEndpoints( this IEndpointRouteBuilder routes,
																SchedulingEndpointOptions? options = null )
	{
		options ??= new SchedulingEndpointOptions();

		// The policies are the host's transport gate: they decide who may call this API at all.
		// They are not the grant decisions, which stay in IScheduler: it enforces them whoever the
		// caller is, so an in-process caller is held to the same rules as one arriving here.
		RouteGroupBuilder group = routes.MapGroup( ApiBasePaths.Normalize( options.BasePath, nameof(options.BasePath) ) );

		// An authenticated user is always required, whatever the host's policies; they are added
		// to it, not substituted for it.
		ApiAccess.RequireAuthenticatedPrincipal( group, options.AuthorizationPolicies, nameof(options) );

		// Literal-segment routes are registered before parameterised routes of the same
		// HTTP method to ensure the routing system resolves them correctly.

		group.MapGet( "", GetSchedules )
			.WithName( "GetSchedules" )
			.WithTags( "Schedules" )
			.WithSummary( "Get schedules in a domain" )
			.Produces<IEnumerable<ScheduleInfo>>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		group.MapGet( "{identifier}", GetSchedule )
			.WithName( "GetSchedule" )
			.WithTags( "Schedules" )
			.WithSummary( "Get a schedule by identifier" )
			.Produces<ScheduleInfo>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapGet( "{identifier}/runs", GetScheduleRuns )
			.WithName( "GetScheduleRuns" )
			.WithTags( "Schedules" )
			.WithSummary( "Get the execution history of a schedule" )
			.Produces<IEnumerable<ScheduleRun>>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapPost( "", RegisterSchedule )
			.WithName( "RegisterSchedule" )
			.WithTags( "Schedules" )
			.WithSummary( "Register a new schedule" )
			.Produces<string>( StatusCodes.Status201Created )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		group.MapPost( "{identifier}/pause", PauseSchedule )
			.WithName( "PauseSchedule" )
			.WithTags( "Schedules" )
			.WithSummary( "Suspend firing of a schedule" )
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapPost( "{identifier}/resume", ResumeSchedule )
			.WithName( "ResumeSchedule" )
			.WithTags( "Schedules" )
			.WithSummary( "Lift the suspension of a schedule" )
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapPost( "{identifier}/run", RunScheduleNow )
			.WithName( "RunScheduleNow" )
			.WithTags( "Schedules" )
			.WithSummary( "Request immediate execution of a schedule" )
			// Accepted, not Ok: the request is only recorded — execution happens when a worker
			// next claims the schedule. Conflict: a run of the schedule is in progress.
			.Produces( StatusCodes.Status202Accepted )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound )
			.Produces<RunInProgressResultInfo>( StatusCodes.Status409Conflict );

		group.MapPut( "{identifier}", UpdateScheduleDefinition )
			.WithName( "UpdateScheduleDefinition" )
			.WithTags( "Schedules" )
			.WithSummary( "Replace the definition of a schedule" )
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapDelete( "{identifier}", DeleteSchedule )
			.WithName( "DeleteSchedule" )
			.WithTags( "Schedules" )
			.WithSummary( "Delete a schedule and its run history" )
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		return group;
	}

	// Owner principals are conveyed as a type/identifier pair of query parameters (Subject
	// convention). Returns false — with the offending argument reported — when only one half of
	// the pair is supplied.
	private static bool TryGetOwner( string? ownerType, string? ownerIdentifier, out Reference? owner,
									out BadRequestResultInfo? error )
	{
		owner = null;
		error = null;

		if( string.IsNullOrEmpty( ownerType ) && string.IsNullOrEmpty( ownerIdentifier ) )
			return true;

		if( string.IsNullOrEmpty( ownerType ) )
		{
			error = new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(ownerType),
											"Owner type is required when an owner identifier is specified." );

			return false;
		}

		if( string.IsNullOrEmpty( ownerIdentifier ) )
		{
			error = new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(ownerIdentifier),
											"Owner identifier is required when an owner type is specified." );

			return false;
		}

		owner = new Reference
		{
			Type = ownerType,
			Identifier = ownerIdentifier
		};

		return true;
	}

	private static async Task<IResult> GetSchedules(
		// Nullable so an omitted domain reaches the handler and is reported as a structured
		// BadRequestResultInfo, rather than failing model binding with an empty 400 body.
		[FromQuery] string? domain,
		[FromQuery] string? ownerType,
		[FromQuery] string? ownerIdentifier,
		[FromQuery] string? jobType,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( domain ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(domain) ) );

		if( !TryGetOwner( ownerType, ownerIdentifier, out Reference? owner, out BadRequestResultInfo? error ) )
			return Results.BadRequest( error );

		return await FacadeResults.TranslateAsync(
								async () => Results.Ok(
										await scheduler.GetSchedulesAsync( domain, owner, jobType ).ConfigureAwait( false ) )
							).ConfigureAwait( false );
	}

	private static async Task<IResult> GetSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		return await FacadeResults.TranslateAsync(
								async () => Results.Ok(
										await scheduler.GetScheduleAsync( identifier ).ConfigureAwait( false ) )
							).ConfigureAwait( false );
	}

	private static async Task<IResult> GetScheduleRuns(
		[FromRoute] string identifier,
		[FromQuery] DateTime? from,
		[FromQuery] DateTime? to,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		return await FacadeResults.TranslateAsync(
								async () => Results.Ok(
										await scheduler.GetRunsAsync( identifier, from, to ).ConfigureAwait( false ) )
							).ConfigureAwait( false );
	}

	private static async Task<IResult> RegisterSchedule(
		[FromBody] ScheduleDefinition? definition,
		[FromQuery] string? ownerType,
		[FromQuery] string? ownerIdentifier,
		[FromServices] IScheduler scheduler )
	{
		if( definition is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(definition) ) );

		if( !TryGetOwner( ownerType, ownerIdentifier, out Reference? owner, out BadRequestResultInfo? error ) )
			return Results.BadRequest( error );

		return await FacadeResults.TranslateAsync(
								async () =>
								{
									// Owner defaults to the calling principal when unspecified; registering on behalf of
									// another principal requires the CREATE_ON_BEHALF grant.
									string identifier = await scheduler.RegisterAsync( definition, owner ).ConfigureAwait( false );

									return Results.Created( string.Empty, identifier );
								}
							).ConfigureAwait( false );
	}

	private static async Task<IResult> UpdateScheduleDefinition(
		[FromRoute] string identifier,
		[FromBody] ScheduleDefinition? definition,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		if( definition is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(definition) ) );

		return await FacadeResults.TranslateAsync(
								async () =>
								{
									await scheduler.UpdateDefinitionAsync( identifier, definition ).ConfigureAwait( false );

									return Results.Ok();
								}
							).ConfigureAwait( false );
	}

	private static async Task<IResult> DeleteSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		return await FacadeResults.TranslateAsync(
								async () =>
								{
									await scheduler.DeleteAsync( identifier ).ConfigureAwait( false );

									return Results.Ok();
								}
							).ConfigureAwait( false );
	}

	private static Task<IResult> PauseSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler ) =>
		SetPausedAsync( identifier, scheduler, paused: true );

	private static Task<IResult> ResumeSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler ) =>
		SetPausedAsync( identifier, scheduler, paused: false );

	private static async Task<IResult> SetPausedAsync( string identifier, IScheduler scheduler, bool paused )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		return await FacadeResults.TranslateAsync(
								async () =>
								{
									if( paused )
										await scheduler.PauseAsync( identifier ).ConfigureAwait( false );
									else
										await scheduler.ResumeAsync( identifier ).ConfigureAwait( false );

									return Results.Ok();
								}
							).ConfigureAwait( false );
	}

	private static async Task<IResult> RunScheduleNow(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrWhiteSpace( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		return await FacadeResults.TranslateAsync(
								async () =>
								{
									await scheduler.RunNowAsync( identifier ).ConfigureAwait( false );

									return Results.Accepted();
								}
							).ConfigureAwait( false );
	}
}
