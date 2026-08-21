using Pug.HttpApiClient;
using Pug.HttpApiClient.Json;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

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

	private static Dictionary<string, string> WorkerQuery( string claimant ) =>
		new() { ["worker"] = claimant ?? string.Empty };

	public Task<IEnumerable<ClaimedJob>> ClaimDueJobsAsync( string claimant, int maximumJobs ) =>
		ApiFailures.TranslateAsync(
				() => HttpClient.PostAsync<JobRunClaimRequest, IEnumerable<ClaimedJob>>(
						ClaimsPath,
						new JobRunClaimRequest { Claimant = claimant, MaximumJobs = maximumJobs } )
			);

	public Task<bool> RenewClaimAsync( string run, string claimant ) =>
		ApiFailures.TranslateAsync(
				// A lost claim comes back as false, not as a failure status: the worker must be
				// able to tell "no longer mine" from "the call did not get through".
				() => HttpClient.PostAsync<object, bool>(
						RunPath( run, "leaseRenewal" ), new object(), queries: WorkerQuery( claimant ) )
			);

	public Task ReportCompletionAsync( string run, string claimant, string result, JobOutcome outcome ) =>
		ApiFailures.TranslateAsync(
				async () =>
				{
					using HttpResponseMessage response =
						await HttpClient.PostAsync(
										RunPath( run, "completion" ),
										new JobRunCompletion { Result = result, Outcome = outcome },
										queries: WorkerQuery( claimant )
									)
									.ConfigureAwait( false );
				},
				() => new UnknownRunException( run )
			);
}
