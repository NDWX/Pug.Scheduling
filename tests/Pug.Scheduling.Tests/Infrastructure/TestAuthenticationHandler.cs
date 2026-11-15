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

	// Optional claim for policy tests: a caller carrying it satisfies a policy that requires
	// ScopeClaim, while the principal's name (and so its grants) is unaffected.
	internal const string ScopeHeader = "X-Test-Scope";
	internal const string ScopeClaim = "scope";

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

		List<Claim> claims = new() { new Claim( ClaimTypes.Name, user.ToString() ) };

		if( Request.Headers.TryGetValue( ScopeHeader, out Microsoft.Extensions.Primitives.StringValues scope ) &&
			!string.IsNullOrEmpty( scope ) )
			claims.Add( new Claim( ScopeClaim, scope.ToString() ) );

		ClaimsIdentity identity = new( claims, SchemeName );

		return Task.FromResult(
				AuthenticateResult.Success( new AuthenticationTicket( new ClaimsPrincipal( identity ), SchemeName ) )
			);
	}
}
