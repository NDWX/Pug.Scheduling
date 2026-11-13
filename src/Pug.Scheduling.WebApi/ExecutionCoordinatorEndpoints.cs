using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi.Models;

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
		routes.MapPost( "executionCoordinator/JobRunClaims", ClaimDueJob )
			.RequireAuthorization()
			.WithName( "ClaimDueJob" )
#if NET7_0_OR_GREATER
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Claim a due job run for a worker" )
#endif
			.Produces<ClaimedJob>( StatusCodes.Status200OK )
			// Nothing due is an expected outcome of a poll, not a failure.
			.Produces( StatusCodes.Status204NoContent )
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
			.Produces( StatusCodes.Status404NotFound )
			.Produces( StatusCodes.Status409Conflict );

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

	// The worker number arrives as a query string; it is parsed here rather than bound as int so
	// that an omitted or malformed value reaches the caller as a structured BadRequestResultInfo
	// instead of model binding failing with an empty 400 body.
	private static IResult? ParseCaller( string? claimant, string? worker, out int workerNumber )
	{
		workerNumber = 0;

		if( string.IsNullOrEmpty( claimant ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(claimant) ) );

		if( string.IsNullOrEmpty( worker ) )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(worker) ) );

		if( !int.TryParse( worker, NumberStyles.None, CultureInfo.InvariantCulture, out workerNumber ) )
			return Results.BadRequest(
					new BadRequestResultInfo(
							"INVALID_ARGUMENT", nameof(worker), "Worker number must be a positive integer." ) );

		return null;
	}

	private static async Task<IResult> ClaimDueJob(
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
			ClaimedJob? claimed =
				await coordinator.ClaimDueJobAsync(
									request.Claimant, request.Worker,
									string.IsNullOrEmpty( request.JobType ) ? null : request.JobType )
								.ConfigureAwait( false );

			return claimed is null ? Results.NoContent() : Results.Ok( claimed );
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
		[FromQuery] string? claimant,
		[FromQuery] string? worker,
		[FromServices] IExecutionCoordinator coordinator )
	{
		IResult? invalid = ParseCaller( claimant, worker, out int workerNumber );

		if( invalid is not null )
			return invalid;

		try
		{
			return Results.Ok(
					await coordinator.RenewClaimAsync( run, claimant!, workerNumber ).ConfigureAwait( false ) );
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
		[FromQuery] string? claimant,
		[FromQuery] string? worker,
		[FromBody] JobRunCompletion? completion,
		[FromServices] IExecutionCoordinator coordinator )
	{
		IResult? invalid = ParseCaller( claimant, worker, out int workerNumber );

		if( invalid is not null )
			return invalid;

		if( completion is null )
			return Results.BadRequest( new BadRequestResultInfo( "MISSING_ARGUMENT", nameof(completion) ) );

		try
		{
			await coordinator.ReportCompletionAsync(
								run, claimant!, workerNumber, completion.Result, completion.Outcome )
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
		catch( ClaimNotHeldException )
		{
			// The reporter cannot be verified to hold the run: adopted by a peer, never claimed
			// by it, or already finished. Nothing was written.
			return Results.Conflict();
		}
		catch( ArgumentException e )
		{
			return InvalidArgument( e );
		}
	}
}
