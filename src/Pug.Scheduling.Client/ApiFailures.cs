using System.Net;
using System.Text.Json;
using Pug.Application.Security;
using Pug.HttpApiClient;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.Client;

/// <summary>
/// Translates the transport's failures back into the exceptions the scheduling abstractions
/// document, so a caller cannot tell an HTTP-bound facade from an in-process one:
/// 403 → <see cref="NotAuthorized"/>, 404 → the caller-supplied unknown-resource exception,
/// 400 → <see cref="ArgumentException"/> rebuilt from the <see cref="BadRequestResultInfo"/> body.
/// 401 is deliberately left as the transport's <c>AuthenticationException</c>: no facade contract
/// covers "you did not authenticate", and it is not the caller's argument that is at fault.
/// Every other failure status surfaces as <see cref="HttpApiRequestException"/>; a call that returns
/// the raw response must pass it through <see cref="EnsureSuccess"/> for that to hold.
/// </summary>
internal static class ApiFailures
{
	private static readonly JsonSerializerOptions JsonOptions = new( JsonSerializerDefaults.Web );

	internal static async Task<T> TranslateAsync<T>( Func<Task<T>> call, Func<Exception>? unknownResource = null )
	{
		try
		{
			return await call().ConfigureAwait( false );
		}
		catch( AuthorizationException )
		{
			throw new NotAuthorized();
		}
		catch( UnknownResourceException ) when( unknownResource is not null )
		{
			throw unknownResource();
		}
		catch( HttpApiRequestException exception ) when( exception.ResponseStatusCode == HttpStatusCode.BadRequest )
		{
			throw AsArgumentException( exception );
		}
	}

	// Pug.HttpApiClient 0.6.0 throws only for the statuses its EnsureSuccessResponse lists, and
	// hands any other failure (503, 504, 429, 408, …) back as a response; only its typed helpers,
	// which deserialize the body, check the status themselves. A call reading a raw response
	// therefore checks it here, or a completion report answered 503 would read as recorded and the
	// worker would drop the run's outcome believing it delivered (D29).
	internal static void EnsureSuccess( HttpResponseMessage response )
	{
		if( !response.IsSuccessStatusCode )
			throw new HttpApiRequestException( response );
	}

	internal static async Task TranslateAsync( Func<Task> call, Func<Exception>? unknownResource = null ) =>
		await TranslateAsync<object?>(
					async () =>
					{
						await call().ConfigureAwait( false );

						return null;
					},
					unknownResource
				).ConfigureAwait( false );

	// The endpoint renders RunInProgressException as RunInProgressResultInfo; a body that does not
	// deserialize still yields the exception, without the details of the run in progress.
	internal static RunInProgressException AsRunInProgressException( string schedule, HttpApiRequestException exception )
	{
		RunInProgressResultInfo? info = null;

		if( !string.IsNullOrWhiteSpace( exception.ResponseMessage ) )
			try
			{
				info = JsonSerializer.Deserialize<RunInProgressResultInfo>( exception.ResponseMessage, JsonOptions );
			}
			catch( JsonException )
			{
				// Not a structured body — the details are simply unknown.
			}

		return info is null
					? new RunInProgressException( schedule )
					: new RunInProgressException( schedule, info.Run, info.Holder, info.LeaseExpiry );
	}

	// The facade reports validation failures as ArgumentException; the endpoint renders those as
	// BadRequestResultInfo, so the client reverses exactly that mapping. A body that does not
	// deserialize still yields an ArgumentException — carrying whatever the server said.
	private static ArgumentException AsArgumentException( HttpApiRequestException exception )
	{
		BadRequestResultInfo? failure = null;

		if( !string.IsNullOrWhiteSpace( exception.ResponseMessage ) )
			try
			{
				failure = JsonSerializer.Deserialize<BadRequestResultInfo>( exception.ResponseMessage, JsonOptions );
			}
			catch( JsonException )
			{
				// Not a structured failure — fall through to the raw response below.
			}

		if( failure is null || string.IsNullOrEmpty( failure.RequestArgument ) )
			return new ArgumentException( exception.ResponseMessage ?? exception.Message );

		return new ArgumentException(
				string.IsNullOrEmpty( failure.Message ) ? failure.Type : failure.Message,
				failure.RequestArgument );
	}
}
