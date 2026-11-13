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
using Pug.Scheduling.Leases;
using Pug.Scheduling.WebApi;
using Pug.Scheduling.Worker.WebApi;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Hosts the scheduling endpoints over a TestServer against real SQLite schedule and lease
/// stores, wired through the production DI extensions — so endpoint tests exercise the same
/// composition a host would use, with only the security principal and the workers faked.
/// </summary>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
	internal const string JobType = "test";
	internal const string Domain = "tenant-1";

	private readonly ScheduleDatabaseFixture _scheduleDatabase = new();
	private readonly LeaseDatabaseFixture _leaseDatabase = new();

	private IHost _host = null!;

	internal TestAuthorizations Authorizations { get; } = new();

	// Claimants of the two hosted fake workers: management grants are per claimant, so each can be
	// granted without the other.
	internal const string WorkerClaimant = "test-worker";
	internal const string OtherWorkerClaimant = "test-worker-2";

	// Worker 1 claims any job type under WorkerClaimant; worker 2 only JobType under
	// OtherWorkerClaimant.
	internal FakeSchedulerWorker Worker { get; } = new( WorkerClaimant, 1 );

	internal FakeSchedulerWorker OtherWorker { get; } = new( OtherWorkerClaimant, 2, JobType );

	internal static JsonSerializerOptions JsonOptions { get; } = new( JsonSerializerDefaults.Web );

	public async Task InitializeAsync()
	{
		await _scheduleDatabase.InitializeAsync();
		await _leaseDatabase.InitializeAsync();

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

		await _leaseDatabase.DisposeAsync();
		await _scheduleDatabase.DisposeAsync();
	}

	private void ConfigureServices( IServiceCollection services )
	{
		services.AddRouting();
		services.AddHttpContextAccessor();

		services.AddAuthentication( TestAuthenticationHandler.SchemeName )
				.AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>(
						TestAuthenticationHandler.SchemeName, _ => { } );

		services.AddAuthorization();

		services.AddSingleton<IApplicationData<IDataSession>>( _scheduleDatabase.ApplicationData );

		services.AddSingleton<IExecutionLeaseProvider>(
				new DatabaseExecutionLeaseProvider( _leaseDatabase.ApplicationData ) );

		// Resolves the calling principal from the current request, so a single registration
		// serves every caller in the authorization matrix.
		services.AddSingleton<ISecurityManager>(
				provider =>
				{
					IHttpContextAccessor httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();

					return SecurityManagerFactory.CreateContextual(
							() => httpContextAccessor.HttpContext?.User.Identity?.Name ?? string.Empty,
							Authorizations.IsGranted
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

	private static void Configure( IApplicationBuilder app )
	{
		app.UseRouting();
		app.UseAuthentication();
		app.UseAuthorization();

		// Each surface carries its own prefix, so they are mapped at the application root — the
		// layout a combined scheduler host uses.
		app.UseEndpoints(
				endpoints =>
				{
					endpoints.MapSchedulingEndpoints();
					endpoints.MapExecutionCoordinatorEndpoints();
					endpoints.MapSchedulerWorkerEndpoints();
				}
			);
	}

	/// <summary>
	/// Client whose requests are authenticated as the given user; pass null for an
	/// unauthenticated caller.
	/// </summary>
	internal HttpClient CreateClient( string? user )
	{
		HttpClient client = _host.GetTestClient();

		if( user is not null )
			client.DefaultRequestHeaders.Add( TestAuthenticationHandler.UserHeader, user );

		return client;
	}

	/// <summary>
	/// <see cref="IHttpApiClient"/> over the TestServer, authenticated as the given user — what
	/// the scheduling API clients are constructed with.
	/// </summary>
	internal IHttpApiClient CreateApiClient( string? user ) =>
		new HttpApiClient.HttpApiClient( new Uri( "http://localhost/" ), new TestHttpClientFactory( this, user ) );

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
