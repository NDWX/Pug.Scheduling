using Pug.Application.Security;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Mutable ACL grant set backing the test security manager. Grants are keyed by principal,
/// operation, object type, domain and — optionally — resource identifier: an identifier-less
/// grant behaves like a domain-wide role grant (what schedule ACLs are about), while an
/// identifier-scoped grant covers only that resource (what worker and coordinator grants are
/// about, being qualified by claimant identity). Nothing is granted by default, so any access a
/// test does not explicitly grant must be carried by the owner rule.
/// </summary>
internal sealed class TestAuthorizations
{
	private readonly HashSet<string> _grants = new();

	internal void Grant( string user, string operation, string objectType, string domain = "",
						string identifier = "" ) =>
		_grants.Add( Key( user, operation, objectType, domain, identifier ) );

	internal bool IsGranted( string user, string operation, NounQualifier resource )
	{
		string domain = resource.Domain ?? string.Empty;

		return _grants.Contains( Key( user, operation, resource.Type, domain, resource.Identifier ?? string.Empty ) ) ||
			_grants.Contains( Key( user, operation, resource.Type, domain, string.Empty ) );
	}

	private static string Key( string user, string operation, string objectType, string domain, string identifier ) =>
		$"{user}|{operation}|{objectType}|{domain}|{identifier}";
}
