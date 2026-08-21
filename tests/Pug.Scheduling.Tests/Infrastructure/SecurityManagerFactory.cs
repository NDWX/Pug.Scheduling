using Moq;
using Pug.Application.Security;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Creates <see cref="ISecurityManager"/> instances with canned authorization decisions, so
/// tests can exercise the facade with and without ACL grants (the owner rule must hold even
/// when every ACL check denies).
/// </summary>
internal static class SecurityManagerFactory
{
	internal static ISecurityManager CreateAlwaysAuthorized( string userId = "test-user" ) =>
		Create( userId, authorized: true );

	internal static ISecurityManager CreateAlwaysDenied( string userId = "test-user" ) =>
		Create( userId, authorized: false );

	/// <summary>
	/// Creates a security manager reporting no current principal — what a host's implementation
	/// yields for in-process calls made outside any authenticated request (a worker's polls, for
	/// instance).
	/// </summary>
	internal static ISecurityManager CreateWithoutPrincipal()
	{
		Mock<ISecurityManager> manager = new();
		manager.Setup( x => x.CurrentPrincipal ).Returns( (IPrincipal)null! );

		return manager.Object;
	}

	/// <summary>
	/// Creates a security manager whose principal and authorization decisions are resolved per
	/// call, so a single registration can serve requests made by different callers (endpoint
	/// tests) rather than one fixed user.
	/// </summary>
	internal static ISecurityManager CreateContextual( Func<string> currentUser,
													Func<string, string, NounQualifier, bool> isAuthorized )
	{
		Mock<IPrincipalIdentity> identity = new();
		identity.Setup( x => x.Identifier ).Returns( currentUser );

		Mock<IPrincipal> principal = new();
		principal.Setup( x => x.Identity ).Returns( identity.Object );
		principal
			.Setup( x => x.IsAuthorizedAsync(
				It.IsAny<IDictionary<string, string>>(),
				It.IsAny<string>(), It.IsAny<NounQualifier>(),
				It.IsAny<string>() ) )
			.ReturnsAsync( ( IDictionary<string, string> _, string operation, NounQualifier resource, string _ ) =>
								isAuthorized( currentUser(), operation, resource ) );

		Mock<ISecurityManager> manager = new();
		manager.Setup( x => x.CurrentPrincipal ).Returns( principal.Object );

		return manager.Object;
	}

	private static ISecurityManager Create( string userId, bool authorized )
	{
		Mock<IPrincipalIdentity> identity = new();
		identity.Setup( x => x.Identifier ).Returns( userId );

		Mock<IPrincipal> principal = new();
		principal.Setup( x => x.Identity ).Returns( identity.Object );
		principal
			.Setup( x => x.IsAuthorizedAsync(
				It.IsAny<IDictionary<string, string>>(),
				It.IsAny<string>(), It.IsAny<NounQualifier>(),
				It.IsAny<string>() ) )
			.ReturnsAsync( authorized );

		Mock<ISecurityManager> manager = new();
		manager.Setup( x => x.CurrentPrincipal ).Returns( principal.Object );

		return manager.Object;
	}
}
