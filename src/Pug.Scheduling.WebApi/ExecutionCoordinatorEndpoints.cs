using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.WebApi;

public static class ExecutionCoordinatorEndpoints
{
	/// <summary>
	/// Maps the execution coordinator's endpoints, under their own <c>executionCoordinator/</c>
	/// prefix, onto the given route builder — the transport an externally hosted worker binds its
	/// <see cref="IExecutionCoordinator"/> to. Hosts whose workers all run in-process need not map
	/// them: an in-process worker resolves the coordinator directly.
	/// The contract is <see cref="IExecutionCoordinator"/> unchanged — due-ness, claim semantics,
	/// misfire handling, retry scheduling, and next-occurrence computation stay behind it, and a
	/// remote worker still never reaches the data backend.
	/// Authorization is the coordinator's own, not these endpoints': every operation requires the
	/// relevant <see cref="CoordinatorSecurityOperations"/> grant on the claimant identity being
	/// acted as, enforced inside <see cref="IExecutionCoordinator"/> so an in-process worker is
	/// held to exactly the same rules. These endpoints only require an authenticated caller and
	/// translate the outcome into HTTP results.
	/// </summary>
	public static IEndpointRouteBuilder MapExecutionCoordinatorEndpoints( this IEndpointRouteBuilder routes )
	{
		routes.MapPost( "executionCoordinator/JobRunClaims", ClaimDueJobs )
			.RequireAuthorization()
			.WithName( "ClaimDueJobs" )
#if NET7_0_OR_GREATER
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Claim due job runs for a worker" )
#endif
			.Produces<IEnumerable<ClaimedJob>>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapPost( "executionCoordinator/JobRuns/{run}/leaseRenewal", RenewClaim )
			.RequireAuthorization()
			.WithName( "RenewJobRunClaim" )
#if NET7_0_OR_GREATER
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Extend the lease of a claimed job run" )
#endif
			// The body is the coordinator's own answer: false means the claim was lost and the
			// worker must abandon the run without reporting completion. A lost claim is an
			// expected outcome of a heartbeat, not a request failure, so it is not an error status.
			.Produces<bool>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapPost( "executionCoordinator/JobRuns/{run}/completion", ReportCompletion )
			.RequireAuthorization()
			.WithName( "ReportJobRunCompletion" )
#if NET7_0_OR_GREATER
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Report the terminal state of a claimed job run" )
#endif
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		return routes;
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

	private static async Task<IResult> ClaimDueJobs(
		[FromBody] JobRunClaimRequest? request,
		[FromServices] IExecutionCoordinator coordinator )
	{
		if( request is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(request) ) );

		if( string.IsNullOrEmpty( request.Claimant ) )
			return Results.BadRequest(
					new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(request.Claimant) ) );

		try
		{
			IEnumerable<ClaimedJob> claimed =
				await coordinator.ClaimDueJobsAsync( request.Claimant, request.MaximumJobs )
								.ConfigureAwait( false );

			return Results.Ok( claimed );
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

	private static async Task<IResult> RenewClaim(
		[FromRoute] string run,
		[FromQuery] string? worker,
		[FromServices] IExecutionCoordinator coordinator )
	{
		if( string.IsNullOrEmpty( worker ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(worker) ) );

		try
		{
			return Results.Ok( await coordinator.RenewClaimAsync( run, worker ).ConfigureAwait( false ) );
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

	private static async Task<IResult> ReportCompletion(
		[FromRoute] string run,
		[FromQuery] string? worker,
		[FromBody] JobRunCompletion? completion,
		[FromServices] IExecutionCoordinator coordinator )
	{
		if( string.IsNullOrEmpty( worker ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(worker) ) );

		if( completion is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(completion) ) );

		try
		{
			await coordinator.ReportCompletionAsync( run, worker, completion.Result, completion.Outcome )
							.ConfigureAwait( false );

			return Results.Ok();
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownRunException )
		{
			// Includes runs whose schedule was deleted mid-flight — history goes with the schedule.
			return Results.NotFound();
		}
		catch( ArgumentException e )
		{
			return InvalidArgument( e );
		}
	}
}
