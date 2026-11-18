using Microsoft.AspNetCore.Http;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.WebApi;

/// <summary>
/// The one place the facades' documented exceptions become HTTP results:
/// <see cref="NotAuthorized"/> → 403, <see cref="UnknownScheduleException"/> /
/// <see cref="UnknownRunException"/> → 404, <see cref="ClaimNotHeldException"/> → 409,
/// <see cref="RunInProgressException"/> → 409 with <see cref="RunInProgressResultInfo"/>,
/// <see cref="ArgumentException"/> → 400 <see cref="BadRequestResultInfo"/>. Anything else is a
/// fault and propagates.
/// Every facade call goes through <see cref="TranslateAsync"/>, so no route can miss a mapping
/// (per-handler copies had drifted, answering 500 where the contract says 400). It wraps the
/// facade call alone, not the whole handler, so only an exception the facade threw is translated.
/// </summary>
internal static class FacadeResults
{
	internal static async Task<IResult> TranslateAsync( Func<Task<IResult>> call )
	{
		try
		{
			return await call().ConfigureAwait( false );
		}
		catch( NotAuthorized )
		{
			return Results.Forbid();
		}
		catch( UnknownScheduleException )
		{
			return Results.NotFound();
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
		catch( RunInProgressException exception )
		{
			// Refused, not queued: nothing was recorded.
			return Results.Conflict(
					new RunInProgressResultInfo(
							exception.Schedule, exception.Run, exception.Holder, exception.LeaseExpiry ) );
		}
		catch( ArgumentException exception )
		{
			return Results.BadRequest(
					new BadRequestResultInfo
					{
						Type = "INVALID_ARGUMENT",
						RequestArgument = exception.ParamName ?? string.Empty,
						Message = exception.Message
					}
				);
		}
	}
}
