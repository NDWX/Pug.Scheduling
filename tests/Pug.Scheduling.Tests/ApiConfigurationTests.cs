using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Client;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.WebApi;
using Pug.Scheduling.WebApi.Models;
using Pug.Scheduling.Worker.WebApi;

namespace Pug.Scheduling.Tests;

/// <summary>
/// The host-configurable parts of the three APIs: base path and authorization policies. Each test
/// hosts the APIs with its own options, since the mapping is fixed once the host is built.
/// </summary>
public class ApiConfigurationTests
{
	private const string User = "config-user";
	private const string OtherUser = "config-other";
	private const string PolicyName = "scheduling-api";

	private static async Task WithFixtureAsync( SchedulingApiFixture fixture, Func<SchedulingApiFixture, Task> test )
	{
		await fixture.InitializeAsync();

		try
		{
			await test( fixture );
		}
		finally
		{
			await fixture.DisposeAsync();
		}
	}

	private static SchedulingApiFixture CustomPathsFixture() =>
		new(
				new SchedulingEndpointOptions { BasePath = "api/v1/schedules" },
				new ExecutionCoordinatorEndpointOptions { BasePath = "/internal/coordinator/" },
				new SchedulerWorkerEndpointOptions { BasePath = "ops/workers" },
				null );

	// A host-registered policy requiring a claim, which the test caller carries only when it sends
	// the scope header.
	private static SchedulingApiFixture PolicyFixture() =>
		new(
				new SchedulingEndpointOptions { AuthorizationPolicies = new[] { PolicyName } },
				new ExecutionCoordinatorEndpointOptions { AuthorizationPolicies = new[] { PolicyName } },
				new SchedulerWorkerEndpointOptions { AuthorizationPolicies = new[] { PolicyName } },
				options => options.AddPolicy(
						PolicyName, policy => policy.RequireClaim( TestAuthenticationHandler.ScopeClaim, "scheduling" ) ) );

	private static ScheduleDefinition NewDefinition() =>
		new()
		{
			Domain = SchedulingApiFixture.Domain,
			Name = "Configured schedule",
			JobType = SchedulingApiFixture.JobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

	[Fact]
	public Task EveryApiAnswersUnderItsCustomBasePathAndNotUnderTheDefault() =>
		WithFixtureAsync(
				CustomPathsFixture(),
				async fixture =>
				{
					using HttpClient client = fixture.CreateClient( User );

					HttpResponseMessage created = await client.PostAsync(
							"/api/v1/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) );

					Assert.Equal( HttpStatusCode.Created, created.StatusCode );

					string identifier = await SchedulingApiFixture.ReadAsync<string>( created );

					Assert.Equal(
							HttpStatusCode.OK, ( await client.GetAsync( $"/api/v1/schedules/{identifier}" ) ).StatusCode );
					Assert.Equal(
							HttpStatusCode.NotFound, ( await client.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );

					// No grant: 403 proves the coordinator route is mapped there (an unmapped one is 404).
					JobRunClaimRequest claim = new() { Claimant = "nobody", Worker = 1 };

					Assert.Equal(
							HttpStatusCode.Forbidden,
							( await client.PostAsync(
									"/internal/coordinator/JobRunClaims", SchedulingApiFixture.JsonContent( claim ) ) )
							.StatusCode );
					Assert.Equal(
							HttpStatusCode.NotFound,
							( await client.PostAsync(
									"/executionCoordinator/JobRunClaims", SchedulingApiFixture.JsonContent( claim ) ) )
							.StatusCode );

					Assert.Equal( HttpStatusCode.OK, ( await client.GetAsync( "/ops/workers" ) ).StatusCode );
					Assert.Equal( HttpStatusCode.NotFound, ( await client.GetAsync( "/workers" ) ).StatusCode );
				} );

	[Fact]
	public Task ClientsRoundTripAgainstACustomBasePath() =>
		WithFixtureAsync(
				CustomPathsFixture(),
				async fixture =>
				{
					const string worker = "config-worker";

					foreach( string operation in new[] { CoordinatorSecurityOperations.ClaimJobs, CoordinatorSecurityOperations.ReportCompletion } )
						fixture.Authorizations.Grant(
								worker, operation, SecurityObjectTypes.ExecutionCoordinator, identifier: worker );

					// Slashes around the client's base path are ignored, as they are on the server.
					IScheduler scheduler = new SchedulerClient( fixture.CreateApiClient( User ), "/api/v1/schedules/" );
					IExecutionCoordinator coordinator =
						new ExecutionCoordinatorClient( fixture.CreateApiClient( worker ), "internal/coordinator" );

					string identifier = await scheduler.RegisterAsync( NewDefinition() );

					Assert.Equal( identifier, ( await scheduler.GetScheduleAsync( identifier ) ).Identifier );

					await scheduler.RunNowAsync( identifier );

					ClaimedJob? job = await coordinator.ClaimDueJobAsync( worker, 1, null );

					Assert.NotNull( job );
					Assert.Equal( identifier, job.Context.Schedule );

					await coordinator.ReportCompletionAsync(
							job.Context.Run, worker, 1, RunResults.Succeeded, new JobOutcome { Successful = true } );

					// A client left on the default base path finds nothing there.
					IScheduler defaultPath = new SchedulerClient( fixture.CreateApiClient( User ) );

					await Assert.ThrowsAnyAsync<Exception>( () => defaultPath.GetScheduleAsync( identifier ) );
				} );

	[Fact]
	public Task WithoutPolicyAnAuthenticatedCallerPasses() =>
		WithFixtureAsync(
				new SchedulingApiFixture(),
				async fixture =>
				{
					using HttpClient client = fixture.CreateClient( User );

					Assert.Equal(
							HttpStatusCode.Created,
							( await client.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) ) )
							.StatusCode );
				} );

	[Fact]
	public Task PolicyGatesEveryApiBeforeTheFacadeIsReached() =>
		WithFixtureAsync(
				PolicyFixture(),
				async fixture =>
				{
					using HttpClient withoutClaim = fixture.CreateClient( User );

					// Authenticated, but lacking the claim: forbidden by the policy; unauthenticated stays 401.
					Assert.Equal(
							HttpStatusCode.Forbidden,
							( await withoutClaim.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) ) )
							.StatusCode );
					Assert.Equal(
							HttpStatusCode.Forbidden,
							( await withoutClaim.PostAsync(
									"/executionCoordinator/JobRunClaims",
									SchedulingApiFixture.JsonContent( new JobRunClaimRequest { Claimant = User, Worker = 1 } ) ) )
							.StatusCode );
					Assert.Equal( HttpStatusCode.Forbidden, ( await withoutClaim.GetAsync( "/workers" ) ).StatusCode );

					using HttpClient anonymous = fixture.CreateClient( null );

					Assert.Equal( HttpStatusCode.Unauthorized, ( await anonymous.GetAsync( "/workers" ) ).StatusCode );

					// The refused registration never reached the facade: nothing was registered.
					fixture.Authorizations.Grant(
							User, SecurityOperations.List, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

					using HttpClient withClaim = fixture.CreateClient( User, "scheduling" );

					HttpResponseMessage list = await withClaim.GetAsync( $"/schedules?domain={SchedulingApiFixture.Domain}" );

					Assert.Equal( HttpStatusCode.OK, list.StatusCode );
					Assert.Empty( await SchedulingApiFixture.ReadAsync<ScheduleInfo[]>( list ) );
				} );

	[Fact]
	public Task PolicyDoesNotReplaceTheFacadeGrants() =>
		WithFixtureAsync(
				PolicyFixture(),
				async fixture =>
				{
					using HttpClient owner = fixture.CreateClient( User, "scheduling" );

					HttpResponseMessage created = await owner.PostAsync(
							"/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) );

					Assert.Equal( HttpStatusCode.Created, created.StatusCode );

					string identifier = await SchedulingApiFixture.ReadAsync<string>( created );

					// Through the gate, but another user holds no grant: the facade still refuses.
					using HttpClient other = fixture.CreateClient( OtherUser, "scheduling" );

					Assert.Equal( HttpStatusCode.Forbidden, ( await other.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );

					fixture.Authorizations.Grant(
							OtherUser, SecurityOperations.GetInfo, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

					Assert.Equal( HttpStatusCode.OK, ( await other.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );
				} );

	public static IEnumerable<object[]> InvalidBasePaths() =>
		new[] { "", " ", "/", "//" }.Select( basePath => new object[] { basePath } );

	[Theory]
	[MemberData( nameof(InvalidBasePaths) )]
	public void InvalidBasePathIsRejectedWhenMappedAndWhenAClientIsBuilt( string basePath )
	{
		IEndpointRouteBuilder routes = new BareRouteBuilder();

		Assert.Throws<ArgumentException>(
				() => routes.MapSchedulingEndpoints( new SchedulingEndpointOptions { BasePath = basePath } ) );
		Assert.Throws<ArgumentException>(
				() => routes.MapExecutionCoordinatorEndpoints(
						new ExecutionCoordinatorEndpointOptions { BasePath = basePath } ) );
		Assert.Throws<ArgumentException>(
				() => routes.MapSchedulerWorkerEndpoints( new SchedulerWorkerEndpointOptions { BasePath = basePath } ) );

		Assert.Throws<ArgumentException>( () => new SchedulerClient( null!, basePath ) );
		Assert.Throws<ArgumentException>( () => new ExecutionCoordinatorClient( null!, basePath ) );
	}

	[Theory]
	[InlineData( "" )]
	[InlineData( "  " )]
	public void BlankPolicyNameIsRejectedWhenMapped( string policy )
	{
		IEndpointRouteBuilder routes = new BareRouteBuilder();
		string[] policies = { PolicyName, policy };

		Assert.Throws<ArgumentException>(
				() => routes.MapSchedulingEndpoints( new SchedulingEndpointOptions { AuthorizationPolicies = policies } ) );
		Assert.Throws<ArgumentException>(
				() => routes.MapExecutionCoordinatorEndpoints(
						new ExecutionCoordinatorEndpointOptions { AuthorizationPolicies = policies } ) );
		Assert.Throws<ArgumentException>(
				() => routes.MapSchedulerWorkerEndpoints(
						new SchedulerWorkerEndpointOptions { AuthorizationPolicies = policies } ) );
	}

	// Just enough of a host to map endpoints onto, for the tests that only watch the mapping throw.
	private sealed class BareRouteBuilder : IEndpointRouteBuilder
	{
		public BareRouteBuilder()
		{
			ServiceProvider = new ServiceCollection().AddRouting().BuildServiceProvider();
		}

		public IServiceProvider ServiceProvider { get; }

		public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();

		public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder( ServiceProvider );
	}
}
