using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Authenticates the caller named by the <c>X-Test-User</c> request header, so endpoint tests can
/// exercise the authorization matrix (owner / other user / domain admin) by varying one header.
/// Requests without the header stay unauthenticated, exercising the endpoints'
/// <c>RequireAuthorization()</c>.
/// </summary>
internal sealed class TestAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
	internal const string SchemeName = "Test";
	internal const string UserHeader = "X-Test-User";

	public TestAuthenticationHandler( IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger,
									UrlEncoder encoder )
		: base( options, logger, encoder )
	{
	}

	protected override Task<AuthenticateResult> HandleAuthenticateAsync()
	{
		if( !Request.Headers.TryGetValue( UserHeader, out Microsoft.Extensions.Primitives.StringValues user ) ||
			string.IsNullOrEmpty( user ) )
			return Task.FromResult( AuthenticateResult.NoResult() );

		ClaimsIdentity identity = new( new[] { new Claim( ClaimTypes.Name, user.ToString() ) }, SchemeName );

		return Task.FromResult(
				AuthenticateResult.Success( new AuthenticationTicket( new ClaimsPrincipal( identity ), SchemeName ) )
			);
	}
}
