using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

	// A host policy that admits everyone, anonymous callers included.
	private static SchedulingApiFixture PermissivePolicyFixture() =>
		new(
				new SchedulingEndpointOptions { AuthorizationPolicies = new[] { PermissivePolicy } },
				new ExecutionCoordinatorEndpointOptions { AuthorizationPolicies = new[] { PermissivePolicy } },
				new SchedulerWorkerEndpointOptions { AuthorizationPolicies = new[] { PermissivePolicy } },
				options => options.AddPolicy( PermissivePolicy, policy => policy.RequireAssertion( _ => true ) ) );

	private const string PermissivePolicy = "anyone";

	// The worker claimant whose grants an anonymous or unmapped caller would act under, were it let
	// through; it is granted everything, so only the gate can refuse.
	private const string Claimant = "gated-worker";

	// The claim a policy test requires; the legitimate callers of the refusal tests always carry it,
	// so they pass whichever policy the fixture configures.
	private const string Scope = "scheduling";

	// A schedule due now (run-now by its owner), so a claim that reached the coordinator would take it.
	private static async Task<string> RegisterDueScheduleAsync( SchedulingApiFixture fixture )
	{
		using HttpClient owner = fixture.CreateClient( User, Scope );

		HttpResponseMessage created = await owner.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) );

		Assert.Equal( HttpStatusCode.Created, created.StatusCode );

		string identifier = await SchedulingApiFixture.ReadAsync<string>( created );

		Assert.Equal( HttpStatusCode.Accepted, ( await owner.PostAsync( $"/schedules/{identifier}/run", null ) ).StatusCode );

		return identifier;
	}

	// Every API refuses the client with the status given, and the refused calls changed nothing: the
	// due schedule is still unclaimed (a granted worker claims it afterwards), no schedule was
	// registered, and the hosted worker was not paused.
	private static async Task AssertEveryApiRefusesAsync( SchedulingApiFixture fixture, HttpClient client,
														HttpStatusCode refused )
	{
		string due = await RegisterDueScheduleAsync( fixture );

		Assert.Equal(
				refused,
				( await client.PostAsync(
						"/executionCoordinator/JobRunClaims",
						SchedulingApiFixture.JsonContent( new JobRunClaimRequest { Claimant = Claimant, Worker = 1 } ) ) )
				.StatusCode );
		Assert.Equal(
				refused,
				( await client.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( NewDefinition() ) ) ).StatusCode );
		Assert.Equal( refused, ( await client.GetAsync( "/workers" ) ).StatusCode );
		Assert.Equal( refused, ( await client.GetAsync( "/workers/1" ) ).StatusCode );
		Assert.Equal( refused, ( await client.PostAsync( "/workers/1/pause", null ) ).StatusCode );

		Assert.False( fixture.Worker.GetInfo().Paused );

		fixture.Authorizations.Grant(
				User, SecurityOperations.List, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		using HttpClient owner = fixture.CreateClient( User, Scope );

		Assert.Single(
				await SchedulingApiFixture.ReadAsync<ScheduleInfo[]>(
						await owner.GetAsync( $"/schedules?domain={SchedulingApiFixture.Domain}" ) ) );

		// The same claim from an authenticated caller holding the grant succeeds, so the refusal was
		// the gate's, and the due run is still there to take.
		using HttpClient worker = fixture.CreateClient( Claimant, Scope );

		HttpResponseMessage claimed = await worker.PostAsync(
				"/executionCoordinator/JobRunClaims",
				SchedulingApiFixture.JsonContent( new JobRunClaimRequest { Claimant = Claimant, Worker = 1 } ) );

		Assert.Equal( HttpStatusCode.OK, claimed.StatusCode );
		Assert.Equal( due, ( await SchedulingApiFixture.ReadAsync<ClaimedJob>( claimed ) ).Context.Schedule );
	}

	private static void GrantEverything( SchedulingApiFixture fixture, string user )
	{
		foreach( string operation in new[] { CoordinatorSecurityOperations.ClaimJobs, CoordinatorSecurityOperations.ReportCompletion } )
			fixture.Authorizations.Grant( user, operation, SecurityObjectTypes.ExecutionCoordinator, identifier: Claimant );

		foreach( string operation in new[] { WorkerSecurityOperations.GetInfo, WorkerSecurityOperations.Pause } )
			fixture.Authorizations.Grant(
					user, operation, SecurityObjectTypes.SchedulerWorker, identifier: SchedulingApiFixture.WorkerClaimant );
	}

	// #34: a host policy that admits anonymous callers is added to the always-required
	// authenticated user, not substituted for it — the coordinator, which trusts a call without a
	// principal as in-process, is never reached.
	[Fact]
	public Task PermissiveHostPolicyStillRequiresAnAuthenticatedCaller() =>
		WithFixtureAsync(
				PermissivePolicyFixture(),
				async fixture =>
				{
					GrantEverything( fixture, Claimant );

					using HttpClient anonymous = fixture.CreateClient( null );

					await AssertEveryApiRefusesAsync( fixture, anonymous, HttpStatusCode.Unauthorized );

					// The authorization middleware refuses before the request is even read: a
					// malformed body is 401, not binding's 400. The endpoint filter alone would answer
					// 400 here, since binding fails before filters run — so this pins the policy layer,
					// which the assertions above cannot tell apart from the filter.
					Assert.Equal(
							HttpStatusCode.Unauthorized,
							( await anonymous.PostAsync(
									"/executionCoordinator/JobRunClaims",
									new StringContent( "{not json", System.Text.Encoding.UTF8, "application/json" ) ) )
							.StatusCode );
				} );

	// #34: with no policies of its own, an API applies the host's DefaultPolicy, which a host may make
	// permissive. The endpoint filter alone refuses the anonymous caller here — the policy layer is
	// pinned by EveryApiCarriesTheDefaultPolicyWhenNoPoliciesAreGiven, not by this test.
	[Fact]
	public Task AnonymousCallerUnderAPermissiveDefaultPolicyIsRefusedByTheFilter() =>
		WithFixtureAsync(
				new SchedulingApiFixture(
						null, null, null,
						options => options.DefaultPolicy = new AuthorizationPolicyBuilder().RequireAssertion( _ => true ).Build() ),
				async fixture =>
				{
					GrantEverything( fixture, Claimant );

					using HttpClient anonymous = fixture.CreateClient( null );

					await AssertEveryApiRefusesAsync( fixture, anonymous, HttpStatusCode.Unauthorized );
				} );

	// With no policies of its own, an API honours the host's DefaultPolicy, however strict: an
	// authenticated caller lacking its claim is forbidden, though it holds every grant behind it.
	[Fact]
	public Task StrictDefaultPolicyIsHonouredWhenNoPoliciesAreGiven() =>
		WithFixtureAsync(
				new SchedulingApiFixture(
						null, null, null,
						options => options.DefaultPolicy = new AuthorizationPolicyBuilder()
															.RequireAuthenticatedUser()
															.RequireClaim( TestAuthenticationHandler.ScopeClaim, Scope )
															.Build() ),
				async fixture =>
				{
					GrantEverything( fixture, Claimant );
					GrantEverything( fixture, OtherUser );

					using HttpClient withoutClaim = fixture.CreateClient( OtherUser );

					await AssertEveryApiRefusesAsync( fixture, withoutClaim, HttpStatusCode.Forbidden );
				} );

	// The policy layer, read from the mapped endpoints themselves: the behavioural tests cannot tell
	// it apart from the endpoint filter, which refuses the same anonymous callers.
	private static void AssertEveryApiEndpoint( SchedulingApiFixture fixture, Action<Endpoint> assert )
	{
		foreach( string basePath in new[] { ApiBasePaths.Schedules, ApiBasePaths.ExecutionCoordinator, ApiBasePaths.Workers } )
		{
			RouteEndpoint[] endpoints = fixture.Endpoints
												.OfType<RouteEndpoint>()
												.Where( endpoint => endpoint.RoutePattern.RawText!.TrimStart( '/' )
																			.StartsWith( basePath, StringComparison.Ordinal ) )
												.ToArray();

			Assert.NotEmpty( endpoints );

			foreach( RouteEndpoint endpoint in endpoints )
				assert( endpoint );
		}
	}

	// No names: the host's DefaultPolicy (authorize data naming no policy), and no inline policy
	// that would displace it.
	[Fact]
	public Task EveryApiCarriesTheDefaultPolicyWhenNoPoliciesAreGiven() =>
		WithFixtureAsync(
				new SchedulingApiFixture(),
				fixture =>
				{
					AssertEveryApiEndpoint(
							fixture,
							endpoint =>
							{
								Assert.Contains(
										endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
										data => data.Policy is null && data.Roles is null && data.AuthenticationSchemes is null );
								Assert.Empty( endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>() );
							} );

					return Task.CompletedTask;
				} );

	// Names: an inline policy denying anonymous callers, plus every named policy.
	[Fact]
	public Task EveryApiCarriesTheAuthenticatedUserPolicyAndTheNamedPolicies() =>
		WithFixtureAsync(
				PolicyFixture(),
				fixture =>
				{
					AssertEveryApiEndpoint(
							fixture,
							endpoint =>
							{
								Assert.Contains(
										endpoint.Metadata.GetOrderedMetadata<AuthorizationPolicy>(),
										policy => policy.Requirements.OfType<DenyAnonymousAuthorizationRequirement>().Any() );
								Assert.Equal(
										new[] { PolicyName },
										endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
												.Where( data => data.Policy is not null )
												.Select( data => data.Policy ) );
							} );

					return Task.CompletedTask;
				} );

	// #34: AllowAnonymous() on a returned group switches every policy off; the endpoint filter still
	// refuses the anonymous caller.
	[Fact]
	public Task AllowAnonymousOnTheGroupIsStillRefusedByTheFilter() =>
		WithFixtureAsync(
				new SchedulingApiFixture( null, null, null, null, group => group.AllowAnonymous() ),
				async fixture =>
				{
					GrantEverything( fixture, Claimant );

					using HttpClient anonymous = fixture.CreateClient( null );

					await AssertEveryApiRefusesAsync( fixture, anonymous, HttpStatusCode.Unauthorized );
				} );

	// #34: authenticated, but the host maps the caller to no principal. Forbidden on every API, even
	// though grants exist under the caller's name — and no longer a 500 on the worker endpoints.
	[Fact]
	public Task AuthenticatedCallerWithoutPrincipalIsForbiddenOnEveryApi() =>
		WithFixtureAsync(
				new SchedulingApiFixture(),
				async fixture =>
				{
					GrantEverything( fixture, Claimant );
					GrantEverything( fixture, SchedulingApiFixture.UnmappedUser );

					using HttpClient unmapped = fixture.CreateClient( SchedulingApiFixture.UnmappedUser );

					await AssertEveryApiRefusesAsync( fixture, unmapped, HttpStatusCode.Forbidden );
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
