using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.AspNetCore.Http;
using Pug.Application.Data;
using Pug.Application.Security;
using Pug.HttpApiClient;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.DependencyInjection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Pug.Scheduling.WebApi;
using Pug.Scheduling.Worker.WebApi;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Hosts the scheduling endpoints over a TestServer against a real SQLite schedule store, wired through the production DI extensions — so endpoint tests exercise the same
/// composition a host would use, with only the security principal and the workers faked.
/// </summary>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
	internal const string JobType = "test";
	internal const string Domain = "tenant-1";

	private readonly SqliteScheduleDatabaseFixture _scheduleDatabase = new();

	private IHost _host = null!;

	// How each API is mapped; null maps it with its defaults, as a host that configures nothing.
	private readonly SchedulingEndpointOptions? _schedulingOptions;
	private readonly ExecutionCoordinatorEndpointOptions? _coordinatorOptions;
	private readonly SchedulerWorkerEndpointOptions? _workerOptions;
	private readonly Action<AuthorizationOptions>? _configureAuthorization;

	// Conventions the host adds to every API's returned route group (AllowAnonymous(), say).
	private readonly Action<RouteGroupBuilder>? _configureGroups;

	public SchedulingApiFixture()
	{
	}

	// For the suites that host the APIs under a custom base path or policies.
	internal SchedulingApiFixture( SchedulingEndpointOptions? scheduling, ExecutionCoordinatorEndpointOptions? coordinator,
									SchedulerWorkerEndpointOptions? workers, Action<AuthorizationOptions>? configureAuthorization,
									Action<RouteGroupBuilder>? configureGroups = null )
	{
		_schedulingOptions = scheduling;
		_coordinatorOptions = coordinator;
		_workerOptions = workers;
		_configureAuthorization = configureAuthorization;
		_configureGroups = configureGroups;
	}

	/// <summary>
	/// An authenticated caller the host's security manager maps to no principal — as a host's
	/// <see cref="ISecurityManager"/> might for a valid token whose subject it does not know.
	/// </summary>
	internal const string UnmappedUser = "unmapped-user";

	internal TestAuthorizations Authorizations { get; } = new();

	// The endpoints the host mapped, for tests that read the authorization metadata of the APIs.
	internal IReadOnlyList<Endpoint> Endpoints => _host.Services.GetRequiredService<EndpointDataSource>().Endpoints;

	// Claimants of the two hosted fake workers: management grants are per claimant, so each can be
	// granted without the other.
	internal const string WorkerClaimant = "test-worker";
	internal const string OtherWorkerClaimant = "test-worker-2";

	// Worker 1 claims any job type under WorkerClaimant; worker 2 only JobType under
	// OtherWorkerClaimant.
	internal FakeSchedulerWorker Worker { get; } = new( WorkerClaimant, 1 );

	internal FakeSchedulerWorker OtherWorker { get; } = new( OtherWorkerClaimant, 2, JobType );

	internal static JsonSerializerOptions JsonOptions { get; } = new( JsonSerializerDefaults.Web );

	// Puts a run of the schedule in progress directly in the store (no worker needed): the state in
	// which run-now is refused.
	internal Task MarkRunInProgressAsync( string schedule ) =>
		_scheduleDatabase.ApplicationData.WriteAsync(
				( session, context ) => session.SetExecutionAsync(
						context,
						new Models.ExecutionLease
						{
							Run = $"run-in-progress-{Guid.NewGuid():N}", ClaimedBy = "elsewhere/1",
							Expiry = DateTime.UtcNow.AddMinutes( 2 )
						} ),
				schedule );

	public async Task InitializeAsync()
	{
		await _scheduleDatabase.InitializeAsync();

		_host = await new HostBuilder()
					.ConfigureWebHost(
							webBuilder => webBuilder
										.UseTestServer()
										.ConfigureServices( ConfigureServices )
										.Configure( Configure )
						)
					.StartAsync();
	}

	public async Task DisposeAsync()
	{
		await _host.StopAsync();
		_host.Dispose();

		await _scheduleDatabase.DisposeAsync();
	}

	private void ConfigureServices( IServiceCollection services )
	{
		services.AddRouting();
		services.AddHttpContextAccessor();

		services.AddAuthentication( TestAuthenticationHandler.SchemeName )
				.AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
						TestAuthenticationHandler.SchemeName, _ => { } );

		services.AddAuthorization( options => _configureAuthorization?.Invoke( options ) );

		services.AddSingleton<IApplicationData<IDataSession>>( _scheduleDatabase.ApplicationData );

		// Resolves the calling principal from the current request, so a single registration
		// serves every caller in the authorization matrix. Like a typical host's, it maps an
		// unauthenticated request to no principal — what the coordinator would take for an
		// in-process caller, were such a request let through — and so does it UnmappedUser.
		services.AddSingleton<ISecurityManager>(
				provider =>
				{
					IHttpContextAccessor httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();

					return SecurityManagerFactory.CreateContextual(
							() => httpContextAccessor.HttpContext?.User.Identity?.Name ?? string.Empty,
							Authorizations.IsGranted,
							() => httpContextAccessor.HttpContext?.User.Identity is { IsAuthenticated: true, Name: var name } &&
								name != UnmappedUser
						);
				} );

		// Every test of a class shares this database, and claims they leave live stay ahead of
		// newer due work in the candidate window; a wide window keeps one test's leftovers from
		// starving the next test's claim.
		services.AddScheduling(
				new SequentialIdGenerator( "schedule" ),
				coordinatorOptions: new ExecutionCoordinatorOptions { ClaimCandidates = 100 } );
		services.AddSchedulableJobType( JobType );

		services.AddSingleton<ISchedulerWorkerRegistry>( new FakeSchedulerWorkerRegistry( Worker, OtherWorker ) );
	}

	private void Configure( IApplicationBuilder app )
	{
		app.UseRouting();
		app.UseAuthentication();
		app.UseAuthorization();

		// Each surface carries its own prefix, so they are mapped at the application root — the
		// layout a combined scheduler host uses.
		app.UseEndpoints(
				endpoints =>
				{
					RouteGroupBuilder[] groups =
					{
						endpoints.MapSchedulingEndpoints( _schedulingOptions ),
						endpoints.MapExecutionCoordinatorEndpoints( _coordinatorOptions ),
						endpoints.MapSchedulerWorkerEndpoints( _workerOptions )
					};

					foreach( RouteGroupBuilder group in groups )
						_configureGroups?.Invoke( group );
				}
			);
	}

	/// <summary>
	/// Client whose requests are authenticated as the given user; pass null for an
	/// unauthenticated caller. <paramref name="scope"/> adds the <c>scope</c> claim a policy test requires.
	/// </summary>
	internal HttpClient CreateClient( string? user, string? scope = null )
	{
		HttpClient client = _host.GetTestClient();

		if( user is not null )
			client.DefaultRequestHeaders.Add( TestAuthenticationHandler.UserHeader, user );

		if( scope is not null )
			client.DefaultRequestHeaders.Add( TestAuthenticationHandler.ScopeHeader, scope );

		return client;
	}

	/// <summary>
	/// <see cref="IHttpApiClient"/> over the TestServer, authenticated as the given user — what
	/// the scheduling API clients are constructed with.
	/// </summary>
	internal IHttpApiClient CreateApiClient( string? user, string? scope = null ) =>
		new HttpApiClient.HttpApiClient( new Uri( "http://localhost/" ), new TestHttpClientFactory( this, user, scope ) );

	internal static HttpContent JsonContent<T>( T value )
	{
		StringContent content = new( JsonSerializer.Serialize( value, JsonOptions ), Encoding.UTF8 );

		content.Headers.ContentType = new MediaTypeHeaderValue( "application/json" );

		return content;
	}

	internal static async Task<T> ReadAsync<T>( HttpResponseMessage response )
	{
		string body = await response.Content.ReadAsStringAsync();

		return JsonSerializer.Deserialize<T>( body, JsonOptions ) ??
			throw new InvalidOperationException( $"Response body did not deserialize to {typeof(T).Name}: {body}" );
	}
}
