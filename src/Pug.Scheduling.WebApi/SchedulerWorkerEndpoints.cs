using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.WebApi;

public static class SchedulerWorkerEndpoints
{
	/// <summary>
	/// Maps management endpoints for the worker instance hosted in this process onto the given
	/// route builder — mount them under their own prefix (e.g. <c>app.MapGroup("/scheduler/worker")</c>),
	/// separate from the schedule endpoints. Hosts that do not run a worker simply do not map
	/// them.
	/// Unlike the schedule endpoints, these enforce authorization themselves:
	/// <see cref="ISchedulerWorker"/> performs none, so every caller requires an explicit
	/// <see cref="WorkerSecurityOperations"/> grant on
	/// <see cref="SecurityObjectTypes.SchedulerWorker"/> qualified by the worker's claimant
	/// identity. There is no owner exemption — worker management is an operator concern.
	/// </summary>
	public static IEndpointRouteBuilder MapSchedulerWorkerEndpoints( this IEndpointRouteBuilder routes )
	{
		routes.MapGet( "", GetWorkerInfo )
			.RequireAuthorization()
			.WithName( "GetSchedulerWorkerInfo" )
#if NET7_0_OR_GREATER
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Get runtime status of the worker hosted in this process" )
#endif
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapPost( "pause", PauseWorker )
			.RequireAuthorization()
			.WithName( "PauseSchedulerWorker" )
#if NET7_0_OR_GREATER
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Stop the worker claiming further jobs" )
#endif
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden );

		routes.MapPost( "resume", ResumeWorker )
			.RequireAuthorization()
			.WithName( "ResumeSchedulerWorker" )
#if NET7_0_OR_GREATER
			.WithTags( "SchedulerWorker" )
			.WithSummary( "Resume job claiming by the worker" )
#endif
			.Produces<WorkerInfo>( StatusCodes.Status200OK )
			.Produces( StatusCodes.Status403Forbidden );

		return routes;
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

	private static async Task<IResult> GetWorkerInfo(
		[FromServices] ISchedulerWorker worker,
		[FromServices] ISecurityManager securityManager )
	{
		// Reading local status is free of side effects, so the snapshot is taken first to supply
		// the claimant the grant is qualified by.
		WorkerInfo info = worker.GetInfo();

		if( !await IsAuthorizedAsync( securityManager, WorkerSecurityOperations.GetInfo, info.Claimant )
				.ConfigureAwait( false ) )
			return Results.Forbid();

		return Results.Ok( info );
	}

	private static Task<IResult> PauseWorker(
		[FromServices] ISchedulerWorker worker,
		[FromServices] ISecurityManager securityManager ) =>
		SetPausedAsync( worker, securityManager, paused: true );

	private static Task<IResult> ResumeWorker(
		[FromServices] ISchedulerWorker worker,
		[FromServices] ISecurityManager securityManager ) =>
		SetPausedAsync( worker, securityManager, paused: false );

	private static async Task<IResult> SetPausedAsync( ISchedulerWorker worker, ISecurityManager securityManager,
													bool paused )
	{
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
