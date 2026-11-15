using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.WebApi;

/// <summary>
/// How the schedule management endpoints are mapped: where, and under which authorization policies.
/// </summary>
public class SchedulingEndpointOptions
{
	/// <summary>
	/// The API's first route segment(s). Leading and trailing <c>/</c> are ignored; it must not be
	/// empty. Clients are constructed with the same base path.
	/// </summary>
	public string BasePath { get; set; } = ApiBasePaths.Schedules;

	/// <summary>
	/// Policies every endpoint of the API requires, applied on the route group. Empty means the
	/// host's <c>DefaultPolicy</c>. Given, they are required together with an authenticated user
	/// (and the <c>DefaultPolicy</c> is not applied). Either way the API's endpoint filter refuses
	/// a caller without authentication (401) or without a principal from the host's
	/// <c>ISecurityManager</c> (403), so no policy can admit one. Names must not be blank; the host
	/// registers the policies themselves.
	/// </summary>
	public IReadOnlyList<string> AuthorizationPolicies { get; set; } = Array.Empty<string>();
}
