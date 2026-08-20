using Pug.Application.Security;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Mutable ACL grant set backing the test security manager: grants are keyed by principal,
/// operation, object type and domain — identifier-level qualification is ignored, so a grant
/// behaves like the domain-wide role grant the authorization matrix is about. Nothing is granted
/// by default, so any access a test does not explicitly grant must be carried by the owner rule.
/// </summary>
internal sealed class TestAuthorizations
{
	private readonly HashSet<string> _grants = new();

	internal void Grant( string user, string operation, string objectType, string domain = "" ) =>
		_grants.Add( Key( user, operation, objectType, domain ) );

	internal bool IsGranted( string user, string operation, NounQualifier resource ) =>
		_grants.Contains( Key( user, operation, resource.Type, resource.Domain ?? string.Empty ) );

	private static string Key( string user, string operation, string objectType, string domain ) =>
		$"{user}|{operation}|{objectType}|{domain}";
}
