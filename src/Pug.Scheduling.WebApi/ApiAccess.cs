using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pug.Application.Security;

namespace Pug.Scheduling.WebApi;

/// <summary>
/// The transport gate every scheduling API group applies: who may call the API at all. It is not
/// a grant decision — the facades (and, for worker management, the endpoint handlers) still make
/// those — but under no supported configuration does a call through these endpoints reach a
/// handler without an authenticated user and a principal. That is what lets
/// <c>ExecutionCoordinator</c> treat a missing principal as an in-process caller (D16) without a
/// call through its endpoints ever being mistaken for one.
/// </summary>
/// <remarks>
/// This file is compiled into <c>Pug.Scheduling.WebApi</c> and, as a linked source file, into
/// <c>Pug.Scheduling.Worker.WebApi</c>: all three APIs share one gate, while a worker host still
/// does not reference this package (D15). Being internal, each assembly carries its own copy.
/// </remarks>
internal static class ApiAccess
{
	// Required alongside the host's named policies, which may admit anonymous callers.
	private static readonly AuthorizationPolicy AuthenticatedUser =
		new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();

	/// <summary>
	/// Applies the policies to every endpoint of the group — with no names, the host's
	/// <c>DefaultPolicy</c>; with names, an authenticated user and every named policy (the
	/// authorization middleware combines them: all must pass) — and adds the endpoint filter that
	/// refuses a call reaching a handler without an authenticated user or without a principal,
	/// whatever the policies admit. Throws <see cref="ArgumentException"/> for a blank policy name.
	/// </summary>
	internal static void RequireAuthenticatedPrincipal( RouteGroupBuilder group, IReadOnlyList<string>? hostPolicies,
														string paramName )
	{
		IReadOnlyList<string> policies = hostPolicies ?? Array.Empty<string>();

		if( policies.Any( string.IsNullOrWhiteSpace ) )
			throw new ArgumentException( "Authorization policy names must not be blank.", paramName );

		// No names: the host's DefaultPolicy, which the host may have made stricter (a scope claim)
		// or bound to its own authentication scheme; displacing it would silently drop both. A
		// permissive DefaultPolicy is left to the filter below.
		// Names: the inline policy keeps an authenticated user required even when a named policy
		// admits anyone. Like any policy object, it also keeps the DefaultPolicy out of the
		// combination, so the host's names are the whole of its say.
		if( policies.Count == 0 )
			group.RequireAuthorization();
		else
		{
			group.RequireAuthorization( AuthenticatedUser );
			group.RequireAuthorization( policies.ToArray() );
		}

		// The guarantee itself, for whatever the policies admit: a permissive DefaultPolicy, or
		// .AllowAnonymous() on the returned group, which makes the authorization middleware skip
		// every policy — endpoint filters still run. It resolves ISecurityManager, so a host mapping
		// these APIs must register one (without it, every request is a 500). Added here, before the host can add its own, so it is the outermost group filter.
		group.AddEndpointFilter(
				async ( context, next ) =>
				{
					HttpContext http = context.HttpContext;

					// The same test as RequireAuthenticatedUser: any authenticated identity. A
					// challenge, not a bare 401, so the host's scheme shapes the answer exactly as
					// it does when the policy refuses.
					if( !http.User.Identities.Any( identity => identity.IsAuthenticated ) )
						return Results.Challenge();

					// Authenticated, but the host's ISecurityManager maps the caller to no principal.
					// Forbidden rather than 401: the credentials are valid, so authenticating again
					// cannot help; the caller is known and refused. Letting it through would present a
					// remote call to the coordinator as an in-process one, exempt from every grant.
					if( http.RequestServices.GetRequiredService<ISecurityManager>().CurrentPrincipal is null )
					{
						http.RequestServices.GetService<ILoggerFactory>()?
							.CreateLogger( typeof(ApiAccess).FullName! )
							.LogWarning(
									"Refused {Path}: an authenticated caller has no principal in the host's security manager.",
									http.Request.Path );

						return Results.Forbid();
					}

					return await next( context ).ConfigureAwait( false );
				} );
	}
}
