using System.Net;
using System.Text.Json;
using Pug.Application.Security;
using Pug.HttpApiClient;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.Client;

/// <summary>
/// Translates the transport's failures back into the exceptions the scheduling abstractions
/// document, so a caller cannot tell an HTTP-bound facade from an in-process one:
/// 403 → <see cref="NotAuthorized"/>, 404 → the caller-supplied unknown-resource exception,
/// 400 → <see cref="ArgumentException"/> rebuilt from the <see cref="BadRequestResultInfo"/> body.
/// 401 is deliberately left as the transport's <c>AuthenticationException</c>: no facade contract
/// covers "you did not authenticate", and it is not the caller's argument that is at fault.
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

	internal static async Task TranslateAsync( Func<Task> call, Func<Exception>? unknownResource = null ) =>
		await TranslateAsync<object?>(
					async () =>
					{
						await call().ConfigureAwait( false );

						return null;
					},
					unknownResource
				).ConfigureAwait( false );

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
