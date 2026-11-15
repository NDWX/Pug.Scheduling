using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.Worker.WebApi;

public static class SchedulerWorkerEndpoints
{
	/// <summary>
	/// Maps management endpoints for the workers hosted in this process, under
	/// <see cref="SchedulerWorkerEndpointOptions.BasePath"/> (default <c>workers</c>), onto the given route builder; individual workers are addressed by
	/// their host-wide worker number through <see cref="ISchedulerWorkerRegistry"/>. They live in
	/// their own package because a worker may be hosted out-of-process, where the host reaches the
	/// scheduler solely through the execution coordinator's transport and has no reason to
	/// reference the scheduler's own WebApi.
	/// Unlike the schedule endpoints, these enforce authorization themselves:
	/// <see cref="ISchedulerWorker"/> performs none, so every caller requires an explicit
	/// <see cref="WorkerSecurityOperations"/> grant on
	/// <see cref="SecurityObjectTypes.SchedulerWorker"/> qualified by the worker's claimant
	/// identity. There is no owner exemption — worker management is an operator concern.
	/// The group also requires an authenticated caller whom the host's
	/// <see cref="ISecurityManager"/> maps to a principal, plus the policies of
	/// <see cref="SchedulerWorkerEndpointOptions.AuthorizationPolicies"/> (the host's
	/// <c>DefaultPolicy</c> when it sets none);
	/// neither a host policy nor <c>AllowAnonymous()</c> on the returned group lifts that (401
	/// without authentication, 403 without a principal), so the grant checks below always have a
	/// principal to ask.
	/// Returns the endpoints' route group so the host can add further conventions to them.
	/// </summary>
	public static RouteGroupBuilder MapSchedulerWorkerEndpoints( this IEndpointRouteBuilder routes,
																	SchedulerWorkerEndpointOptions? options = null )
	{
		options ??= new SchedulerWorkerEndpointOptions();

		// The policies are the host's transport gate: they decide who may call this API at all.
		// They are not the grant decisions: each handler below still checks the caller's
		// WorkerSecurityOperations grant, because ISchedulerWorker performs none of its own.
		RouteGroupBuilder group = routes.MapGroup( ApiBasePaths.Normalize( options.BasePath, nameof(options.BasePath) ) );

		// An authenticated user is always required, whatever the host's policies; they are added
		// to it, not substituted for it.
		ApiAccess.RequireAuthenticatedPrincipal( group, options.AuthorizationPolicies, nameof(options) );

		group.MapGet( "", GetWorkers )
			.WithName( "GetSchedulerWorkers" )
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Get runtime status of the workers hosted in this process that the caller may view" )
			.Produces<WorkerInfo[]>( StatusCodes.Status200OK );

		group.MapGet( "{worker:int}", GetWorkerInfo )
			.WithName( "GetSchedulerWorkerInfo" )
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Get runtime status of a worker hosted in this process" )
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapPost( "{worker:int}/pause", PauseWorker )
			.WithName( "PauseSchedulerWorker" )
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Stop the worker claiming further jobs" )
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		group.MapPost( "{worker:int}/resume", ResumeWorker )
			.WithName( "ResumeSchedulerWorker" )
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Resume job claiming by the worker" )
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden )
			.Produces( StatusCodes.Status404NotFound );

		return group;
	}

	// Worker grants are qualified by claimant identity rather than domain: a worker serves every
	// domain the scheduler hosts, so its management is not tenant-scoped.
	private static async Task<bool> IsAuthorizedAsync( ISecurityManager securityManager, string operation,
													string claimant ) =>
		await securityManager.CurrentPrincipal
							.IsAuthorizedAsync(
									new Dictionary<string, string>( 0 ),
									operation,
									new NounQualifier
									{
										Type = SecurityObjectTypes.SchedulerWorker,
										Identifier = claimant
									},
									string.Empty
								)
							.ConfigureAwait( false );

	// Lists only the workers whose claimant the caller may view; an empty list rather than 403
	// when there are none, since the caller may be entitled to workers another host runs.
	private static async Task<IResult> GetWorkers(
		[FromServices] ISchedulerWorkerRegistry registry,
		[FromServices] ISecurityManager securityManager )
	{
		List<WorkerInfo> visible = new();

		// Grants are per claimant, and a group's workers share one, so each is checked once.
		Dictionary<string, bool> authorized = new();

		foreach( ISchedulerWorker worker in registry.Workers )
		{
			WorkerInfo info = worker.GetInfo();

			if( !authorized.TryGetValue( info.Claimant, out bool granted ) )
			{
				granted = await IsAuthorizedAsync( securityManager, WorkerSecurityOperations.GetInfo, info.Claimant )
							.ConfigureAwait( false );

				authorized[info.Claimant] = granted;
			}

			if( granted )
				visible.Add( info );
		}

		return Results.Ok( visible.ToArray() );
	}

	private static async Task<IResult> GetWorkerInfo(
		int worker,
		[FromServices] ISchedulerWorkerRegistry registry,
		[FromServices] ISecurityManager securityManager )
	{
		ISchedulerWorker? instance = registry.Find( worker );

		if( instance is null )
			return Results.NotFound();

		// Reading local status is free of side effects, so the snapshot is taken first to supply
		// the claimant the grant is qualified by.
		WorkerInfo info = instance.GetInfo();

		if( !await IsAuthorizedAsync( securityManager, WorkerSecurityOperations.GetInfo, info.Claimant )
				.ConfigureAwait( false ) )
			return Results.Forbid();

		return Results.Ok( info );
	}

	private static Task<IResult> PauseWorker(
		int worker,
		[FromServices] ISchedulerWorkerRegistry registry,
		[FromServices] ISecurityManager securityManager ) =>
		SetPausedAsync( registry.Find( worker ), securityManager, paused: true );

	private static Task<IResult> ResumeWorker(
		int worker,
		[FromServices] ISchedulerWorkerRegistry registry,
		[FromServices] ISecurityManager securityManager ) =>
		SetPausedAsync( registry.Find( worker ), securityManager, paused: false );

	private static async Task<IResult> SetPausedAsync( ISchedulerWorker? worker, ISecurityManager securityManager,
													bool paused )
	{
		if( worker is null )
			return Results.NotFound();

		WorkerInfo info = worker.GetInfo();

		string operation = paused ? WorkerSecurityOperations.Pause : WorkerSecurityOperations.Resume;

		if( !await IsAuthorizedAsync( securityManager, operation, info.Claimant ).ConfigureAwait( false ) )
			return Results.Forbid();

		if( paused )
			worker.Pause();
		else
			worker.Resume();

		// Returns the resulting status so callers observe the effect without a second request.
		return Results.Ok( worker.GetInfo() );
	}
}
