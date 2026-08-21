using Pug.Effable;
using Pug.HttpApiClient;
using Pug.HttpApiClient.Json;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Client;

/// <summary>
/// <see cref="IScheduler"/> over the routes mapped by <c>MapSchedulingEndpoints()</c>. Callers
/// bind this in place of the in-process facade; the contract — including which exceptions surface
/// — is identical, so nothing above it needs to know where the scheduler runs.
/// The underlying <see cref="IHttpApiClient"/> carries the caller's credentials (its decorators
/// are where authentication is configured); authorization itself remains the server-side
/// facade's, owner rule included.
/// </summary>
public class SchedulerClient : ApiClient, IScheduler
{
	private const string SchedulesPath = "schedules";

	public SchedulerClient( IHttpApiClient httpClient )
		: base( httpClient )
	{
	}

	private static string SchedulePath( string identifier ) =>
		$"{SchedulesPath}/{Uri.EscapeDataString( identifier )}";

	// Pug.HttpApiClient 0.6.0 aggregates the query collection without a seed, so an empty (but
	// non-null) dictionary throws where null is handled: hand it null when there is nothing to ask.
	private static IDictionary<string, string>? Queries( Dictionary<string, string> queries ) =>
		queries.Count > 0 ? queries : null;

	// The owner principal travels as a type/identifier query pair, as the endpoints expect.
	private static void AddOwner( IDictionary<string, string> queries, Reference? owner )
	{
		if( owner is null )
			return;

		queries["ownerType"] = owner.Type;
		queries["ownerIdentifier"] = owner.Identifier;
	}

	public Task<string> RegisterAsync( ScheduleDefinition definition, Reference? owner = null )
	{
		Dictionary<string, string> queries = new();

		AddOwner( queries, owner );

		return ApiFailures.TranslateAsync(
				() => HttpClient.PostAsync<ScheduleDefinition, string>(
						SchedulesPath, definition, queries: Queries( queries ) )
			);
	}

	public Task<IEnumerable<ScheduleInfo>> GetSchedulesAsync( string domain, Reference? owner = null,
															string? jobType = null )
	{
		Dictionary<string, string> queries = new() { ["domain"] = domain ?? string.Empty };

		AddOwner( queries, owner );

		if( !string.IsNullOrEmpty( jobType ) )
			queries["jobType"] = jobType;

		return ApiFailures.TranslateAsync(
				() => HttpClient.GetAsync<IEnumerable<ScheduleInfo>>( SchedulesPath, queries: Queries( queries ) )
			);
	}

	public Task<ScheduleInfo> GetScheduleAsync( string identifier ) =>
		ApiFailures.TranslateAsync(
				() => HttpClient.GetAsync<ScheduleInfo>( SchedulePath( identifier ) ),
				() => new UnknownScheduleException( identifier )
			);

	public Task UpdateDefinitionAsync( string identifier, ScheduleDefinition definition ) =>
		ApiFailures.TranslateAsync(
				async () =>
				{
					using HttpResponseMessage response =
						await HttpClient.PutAsync( SchedulePath( identifier ), definition )
										.ConfigureAwait( false );
				},
				() => new UnknownScheduleException( identifier )
			);

	public Task DeleteAsync( string identifier ) =>
		ApiFailures.TranslateAsync(
				async () =>
				{
					using HttpResponseMessage response =
						await HttpClient.DeleteAsync( SchedulePath( identifier ) ).ConfigureAwait( false );
				},
				() => new UnknownScheduleException( identifier )
			);

	public Task PauseAsync( string identifier ) => PostAsync( identifier, "pause" );

	public Task ResumeAsync( string identifier ) => PostAsync( identifier, "resume" );

	public Task RunNowAsync( string identifier ) => PostAsync( identifier, "run" );

	private Task PostAsync( string identifier, string action ) =>
		ApiFailures.TranslateAsync(
				async () =>
				{
					// These actions take no request body; an empty JSON object keeps the request
					// well-formed for the media type.
					using HttpResponseMessage response =
						await HttpClient.PostAsync( $"{SchedulePath( identifier )}/{action}", new object() )
										.ConfigureAwait( false );
				},
				() => new UnknownScheduleException( identifier )
			);

	public Task<IEnumerable<ScheduleRun>> GetRunsAsync( string identifier, DateTime? from = null,
														DateTime? to = null )
	{
		Dictionary<string, string> queries = new();

		// Round-trip format so the server reads back the exact instant, kind included.
		if( from is not null )
			queries["from"] = from.Value.ToString( "o" );

		if( to is not null )
			queries["to"] = to.Value.ToString( "o" );

		return ApiFailures.TranslateAsync(
				() => HttpClient.GetAsync<IEnumerable<ScheduleRun>>(
						$"{SchedulePath( identifier )}/runs", queries: Queries( queries ) ),
				() => new UnknownScheduleException( identifier )
			);
	}
}
