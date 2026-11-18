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
	/// Maps the execution coordinator's endpoints, under
	/// <see cref="ExecutionCoordinatorEndpointOptions.BasePath"/> (default <c>executionCoordinator</c>), onto the given route builder — the transport an externally hosted worker binds its
	/// <see cref="IExecutionCoordinator"/> to. Hosts whose workers all run in-process need not map
	/// them: an in-process worker resolves the coordinator directly.
	/// The contract is <see cref="IExecutionCoordinator"/> unchanged — due-ness, claim semantics,
	/// misfire handling, retry scheduling, and next-occurrence computation stay behind it, and a
	/// remote worker still never reaches the data backend.
	/// Authorization is the coordinator's own, not these endpoints': every operation requires the
	/// relevant <see cref="CoordinatorSecurityOperations"/> grant on the claimant identity being
	/// acted as, enforced inside <see cref="IExecutionCoordinator"/> so an in-process worker is
	/// held to exactly the same rules. These endpoints only require an authenticated caller whom the
	/// host's <see cref="ISecurityManager"/> maps to a principal, plus the policies of
	/// <see cref="ExecutionCoordinatorEndpointOptions.AuthorizationPolicies"/> (the host's
	/// <c>DefaultPolicy</c> when it sets none),
	/// and translate the outcome into HTTP results. Neither a host policy nor <c>AllowAnonymous()</c>
	/// on the returned group lifts that requirement (401 without authentication, 403 without a
	/// principal): the coordinator skips its grant check for a call without a principal, trusting it
	/// as in-process, so no transport call may ever arrive without one.
	/// Returns the endpoints' route group so the host can add further conventions to them.
	/// </summary>
	public static RouteGroupBuilder MapExecutionCoordinatorEndpoints( this IEndpointRouteBuilder routes,
																								ExecutionCoordinatorEndpointOptions? options = null )
	{
		options ??= new ExecutionCoordinatorEndpointOptions();

		// The policies are the host's transport gate: they decide who may call this API at all.
		// They are not the grant decisions, which stay in IExecutionCoordinator: it enforces them whoever the
		// caller is, so an in-process caller is held to the same rules as one arriving here.
		RouteGroupBuilder group = routes.MapGroup( ApiBasePaths.Normalize( options.BasePath, nameof(options.BasePath) ) );

		// An authenticated user is always required, whatever the host's policies; they are added
		// to it, not substituted for it.
		ApiAccess.RequireAuthenticatedPrincipal( group, options.AuthorizationPolicies, nameof(options) );

		group.MapPost( "JobRunClaims", ClaimDueJob )
			.WithName( "ClaimDueJob" )
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Claim a due job run for a worker" )
			.Produces<ClaimedJob>( StatusCodes.Status200OK )
			// Nothing due is an expected outcome of a poll, not a failure.
			.Produces( StatusCodes.Status204NoContent )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		group.MapPost( "JobRuns/{run}/leaseRenewal", RenewClaim )
			.WithName( "RenewJobRunClaim" )
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Extend the lease of a claimed job run" )
			// The body is the coordinator's own answer: false means the claim was lost and the
			// worker must abandon the run without reporting completion. A lost claim is an
			// expected outcome of a heartbeat, not a request failure, so it is not an error status.
			.Produces<bool>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		group.MapPost( "JobRuns/{run}/abandonment", AbandonClaim )
			.WithName( "AbandonJobRunClaim" )
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Release a claimed job run so a peer can adopt it immediately" )
			// The body is the coordinator's own answer: false means the claim was not held (nothing
			// was released), which is an expected outcome, not a request failure.
			.Produces<bool>( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden );

		group.MapPost( "JobRuns/{run}/completion", ReportCompletion )
			.WithName( "ReportJobRunCompletion" )
			.WithTags( "ExecutionCoordinator" )
			.WithSummary( "Report the terminal state of a claimed job run" )
			.Produces( StatusCodes.Status200OK )
			.Produces<BadRequestResultInfo>( StatusCodes.Status400BadRequest )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound )
			.Produces( StatusCodes.Status409Conflict );

		return group;
	}

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

		return await FacadeFailures.TranslateAsync(
								async () =>
								{
									ClaimedJob? claimed =
										await coordinator.ClaimDueJobAsync(
															request.Claimant, request.Worker,
															string.IsNullOrEmpty( request.JobType ) ? null : request.JobType )
														.ConfigureAwait( false );

									return claimed is null ? Results.NoContent() : Results.Ok( claimed );
								}
							).ConfigureAwait( false );
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

		return await FacadeFailures.TranslateAsync(
								async () => Results.Ok(
										await coordinator.RenewClaimAsync( run, claimant!, workerNumber ).ConfigureAwait( false ) )
							).ConfigureAwait( false );
	}

	private static async Task<IResult> AbandonClaim(
		[FromRoute] string run,
		[FromQuery] string? claimant,
		[FromQuery] string? worker,
		[FromServices] IExecutionCoordinator coordinator )
	{
		IResult? invalid = ParseCaller( claimant, worker, out int workerNumber );

		if( invalid is not null )
			return invalid;

		return await FacadeFailures.TranslateAsync(
								async () => Results.Ok(
										await coordinator.AbandonClaimAsync( run, claimant!, workerNumber ).ConfigureAwait( false ) )
							).ConfigureAwait( false );
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

		return await FacadeFailures.TranslateAsync(
								async () =>
								{
									await coordinator.ReportCompletionAsync(
														run, claimant!, workerNumber, completion.Result, completion.Outcome )
													.ConfigureAwait( false );

									return Results.Ok();
								}
							).ConfigureAwait( false );
	}
}
