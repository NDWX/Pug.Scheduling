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
	/// Maps schedule management API endpoints, under their own <c>schedules/</c> prefix, onto the
	/// given route builder: the host's routes are not assumed to be the scheduler's alone, and
	/// the scheduler host serves the execution coordinator's routes alongside these. Pass
	/// <c>app</c> to mount at the application root, or a <see cref="RouteGroupBuilder"/> from
	/// <c>app.MapGroup("/your/prefix")</c> to mount the whole surface beneath a further prefix.
	/// All endpoints require an authenticated caller; authorization (the schedule owner rule
	/// followed by role-based ACLs) is enforced by <see cref="IScheduler"/> itself, so these
	/// endpoints only translate its outcomes into HTTP results.
	/// Endpoints for externally hosted workers are mapped separately by
	/// <see cref="ExecutionCoordinatorEndpoints.MapExecutionCoordinatorEndpoints"/>; worker
	/// management endpoints live in <c>Pug.Scheduling.Worker.WebApi</c>, so a worker host need
	/// not reference this package at all.
	/// Swagger documentation is surfaced when the host configures <c>AddEndpointsApiExplorer()</c>
	/// and <c>AddSwaggerGen()</c>.
	/// </summary>
	public static IEndpointRouteBuilder MapSchedulingEndpoints( this IEndpointRouteBuilder routes )
	{
		// Literal-segment routes are registered before parameterised routes of the same
		// HTTP method to ensure the routing system resolves them correctly.

		routes.MapGet( "schedules", GetSchedules )
			.RequireAuthorization()
			.WithName( "GetSchedules" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Get schedules in a domain" )
#endif
			.Produces<IEnumerable<ScheduleInfo>>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapGet( "schedules/{identifier}", GetSchedule )
			.RequireAuthorization()
			.WithName( "GetSchedule" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Get a schedule by identifier" )
#endif
			.Produces<ScheduleInfo>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapGet( "schedules/{identifier}/runs", GetScheduleRuns )
			.RequireAuthorization()
			.WithName( "GetScheduleRuns" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Get the execution history of a schedule" )
#endif
			.Produces<IEnumerable<ScheduleRun>>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapPost( "schedules", RegisterSchedule )
			.RequireAuthorization()
			.WithName( "RegisterSchedule" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Register a new schedule" )
#endif
			.Produces<string>( StatusCodes.Status201Created )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapPost( "schedules/{identifier}/pause", PauseSchedule )
			.RequireAuthorization()
			.WithName( "PauseSchedule" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Suspend firing of a schedule" )
#endif
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapPost( "schedules/{identifier}/resume", ResumeSchedule )
			.RequireAuthorization()
			.WithName( "ResumeSchedule" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Lift the suspension of a schedule" )
#endif
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapPost( "schedules/{identifier}/run", RunScheduleNow )
			.RequireAuthorization()
			.WithName( "RunScheduleNow" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Request immediate execution of a schedule" )
#endif
			// Accepted, not Ok: the request only brings the next occurrence forward — execution
			// happens when a worker next claims the schedule.
			.Produces( StatusCodes.Status202Accepted )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapPut( "schedules/{identifier}", UpdateScheduleDefinition )
			.RequireAuthorization()
			.WithName( "UpdateScheduleDefinition" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Replace the definition of a schedule" )
#endif
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		routes.MapDelete( "schedules/{identifier}", DeleteSchedule )
			.RequireAuthorization()
			.WithName( "DeleteSchedule" )
#if NET7_0_OR_GREATER
			.WithTags( "Schedules" )
			.WithSummary( "Delete a schedule and its run history" )
#endif
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		return routes;
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

	private static IResult InvalidArgument( ArgumentException exception ) =>
		Results.BadRequest(
				new BadRequestResultInfo
				{
					Type = "INVALID_ARGUMENT",
					RequestArgument = exception.ParamName ?? string.Empty,
					Message = exception.Message
				}
			);

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

		try
		{
			IEnumerable<ScheduleInfo> schedules =
				await scheduler.GetSchedulesAsync( domain, owner, jobType ).ConfigureAwait( false );

			return Results.Ok( schedules );
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( ArgumentException e )
		{
			return InvalidArgument( e );
		}
	}

	private static async Task<IResult> GetSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		try
		{
			return Results.Ok( await scheduler.GetScheduleAsync( identifier ).ConfigureAwait( false ) );
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
	}

	private static async Task<IResult> GetScheduleRuns(
		[FromRoute] string identifier,
		[FromQuery] DateTime? from,
		[FromQuery] DateTime? to,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		try
		{
			IEnumerable<ScheduleRun> runs =
				await scheduler.GetRunsAsync( identifier, from, to ).ConfigureAwait( false );

			return Results.Ok( runs );
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
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

		try
		{
			// Owner defaults to the calling principal when unspecified; registering on behalf of
			// another principal requires the CREATE grant.
			string identifier = await scheduler.RegisterAsync( definition, owner ).ConfigureAwait( false );

			return Results.Created( string.Empty, identifier );
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( ArgumentException e )
		{
			return InvalidArgument( e );
		}
	}

	private static async Task<IResult> UpdateScheduleDefinition(
		[FromRoute] string identifier,
		[FromBody] ScheduleDefinition? definition,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		if( definition is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(definition) ) );

		try
		{
			await scheduler.UpdateDefinitionAsync( identifier, definition ).ConfigureAwait( false );

			return Results.Ok();
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
		catch( ArgumentException e )
		{
			return InvalidArgument( e );
		}
	}

	private static async Task<IResult> DeleteSchedule(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		try
		{
			await scheduler.DeleteAsync( identifier ).ConfigureAwait( false );

			return Results.Ok();
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
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
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		try
		{
			if( paused )
				await scheduler.PauseAsync( identifier ).ConfigureAwait( false );
			else
				await scheduler.ResumeAsync( identifier ).ConfigureAwait( false );

			return Results.Ok();
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
	}

	private static async Task<IResult> RunScheduleNow(
		[FromRoute] string identifier,
		[FromServices] IScheduler scheduler )
	{
		if( string.IsNullOrEmpty( identifier ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(identifier) ) );

		try
		{
			await scheduler.RunNowAsync( identifier ).ConfigureAwait( false );

			return Results.Accepted();
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
		}
	}
}
