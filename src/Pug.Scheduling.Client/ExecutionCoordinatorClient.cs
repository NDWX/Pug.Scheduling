using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using Pug.HttpApiClient;
using Pug.HttpApiClient.Json;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi.Models;

namespace Pug.Scheduling.Client;

/// <summary>
/// <see cref="IExecutionCoordinator"/> over the routes mapped by
/// <c>MapExecutionCoordinatorEndpoints()</c> — what an out-of-process worker host binds so its
/// <c>SchedulerWorker</c> runs unchanged against a remote scheduler. Due-ness, claim semantics,
/// misfire handling, retry scheduling, and next-occurrence computation stay on the server, and
/// the worker still never reaches a data store.
/// The <see cref="IHttpApiClient"/>'s decorators carry the worker's credentials; the server-side
/// coordinator authorizes them against the claimant identity being acted as.
/// </summary>
public class ExecutionCoordinatorClient : ApiClient, IExecutionCoordinator
{
	private const string ClaimsPath = "executionCoordinator/JobRunClaims";

	public ExecutionCoordinatorClient( IHttpApiClient httpClient )
		: base( httpClient )
	{
	}

	private static string RunPath( string run, string action ) =>
		$"executionCoordinator/JobRuns/{Uri.EscapeDataString( run )}/{action}";

	private static Dictionary<string, string> CallerQuery( string claimant, int worker ) =>
		new()
		{
			["claimant"] = claimant ?? string.Empty,
			["worker"] = worker.ToString( CultureInfo.InvariantCulture )
		};

	public Task<ClaimedJob?> ClaimDueJobAsync( string claimant, int worker, string? jobType ) =>
		ApiFailures.TranslateAsync<ClaimedJob?>(
				async () =>
				{
					using HttpResponseMessage response =
						await HttpClient.PostAsync(
										ClaimsPath,
										new JobRunClaimRequest { Claimant = claimant, Worker = worker, JobType = jobType }
									)
									.ConfigureAwait( false );

					// 204: nothing is due — the coordinator's null, not a failure.
					if( response.StatusCode == HttpStatusCode.NoContent )
						return null;

					return await response.Content.ReadFromJsonAsync<ClaimedJob>(
												// The options every other response of this client is read with.
												IHttpApiClientJsonExtensions.DefaultJsonSerializerOptions )
										.ConfigureAwait( false );
				}
			);

	public Task<bool> RenewClaimAsync( string run, string claimant, int worker ) =>
		ApiFailures.TranslateAsync(
				// A lost claim comes back as false, not as a failure status: the worker must be
				// able to tell "no longer mine" from "the call did not get through".
				() => HttpClient.PostAsync<object, bool>(
						RunPath( run, "leaseRenewal" ), new object(), queries: CallerQuery( claimant, worker ) )
			);

	public Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome ) =>
		ApiFailures.TranslateAsync(
				async () =>
				{
					try
					{
						using HttpResponseMessage response =
							await HttpClient.PostAsync(
											RunPath( run, "completion" ),
											new JobRunCompletion { Result = result, Outcome = outcome },
											queries: CallerQuery( claimant, worker )
										)
										.ConfigureAwait( false );
					}
					catch( HttpApiRequestException exception ) when( exception.ResponseStatusCode == HttpStatusCode.Conflict )
					{
						throw new ClaimNotHeldException( run );
					}
				},
				() => new UnknownRunException( run )
			);
}
