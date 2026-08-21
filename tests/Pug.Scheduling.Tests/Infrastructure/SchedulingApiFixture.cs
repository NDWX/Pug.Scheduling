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
/// composition a host would use, with only the security principal and the worker faked.
/// </summary>
public sealed class SchedulingApiFixture : IAsyncLifetime
{
	internal const string JobType = "test";
	internal const string Domain = "tenant-1";

	private readonly ScheduleDatabaseFixture _scheduleDatabase = new();
	private readonly LeaseDatabaseFixture _leaseDatabase = new();

	private IHost _host = null!;

	internal TestAuthorizations Authorizations { get; } = new();

	internal FakeSchedulerWorker Worker { get; } = new();

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

		services.AddScheduling( new SequentialIdGenerator( "schedule" ) );
		services.AddSchedulableJobType( JobType );

		services.AddSingleton<ISchedulerWorker>( Worker );
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
