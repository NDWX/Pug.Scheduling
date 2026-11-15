// Program.cs of a remote worker host (scheduling-workers skill): workers that reach the scheduler only
// through its execution coordinator endpoints. Packages: Pug.Scheduling.Worker.DependencyInjection,
// Pug.Scheduling.Client, Microsoft.Extensions.Hosting, Microsoft.Extensions.Http, Pug.HttpApiClient.OAuth2
// (for ClientCredentialsDecorator; any IHttpRequestMessageDecorator that adds credentials will do), and the
// libraries holding your handlers. No store, no Pug.Scheduling, no ASP.NET.
//
// The scheduler host maps MapExecutionCoordinatorEndpoints() and grants each group's service principal
// CLAIM_JOBS, RENEW_CLAIM, ABANDON_CLAIM and REPORT_COMPLETION on { Type = "EXECUTION_COORDINATOR",
// Identifier = <the group's claimant> }.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Pug.HttpApiClient;
using Pug.HttpApiClient.OAuth2;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Client;
using Pug.Scheduling.Worker;
using Pug.Scheduling.Worker.DependencyInjection;

HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );

// The scheduler host's address, including any path base it is served under.
Uri schedulerUrl = new( builder.Configuration[ "Scheduling:Url" ]! );
string coordinatorBasePath = builder.Configuration[ "Scheduling:CoordinatorBasePath" ] ?? "executionCoordinator";
string tokenEndpoint = builder.Configuration[ "Scheduling:TokenEndpoint" ]!;

builder.Services.AddHttpClient();

// One coordinator client per credential. Each group's claimant must be one its credential is granted.
static ExecutionCoordinatorClient Coordinator( IServiceProvider provider, Uri schedulerUrl, string basePath,
												string tokenEndpoint, string clientId, string clientSecret, string scope )
{
	IHttpClientFactory http = provider.GetRequiredService<IHttpClientFactory>();

	return new ExecutionCoordinatorClient(
			new HttpApiClient( schedulerUrl, http,
								messageDecorators: new IHttpRequestMessageDecorator[]
								{
									new ClientCredentialsDecorator( tokenEndpoint, clientId, clientSecret, scope, http )
								} ),
			basePath );   // must match the host's ExecutionCoordinatorEndpointOptions.BasePath
}

// The default coordinator, used by groups registered without a factory.
builder.Services.AddSingleton<IExecutionCoordinator>( p => Coordinator(
		p, schedulerUrl, coordinatorBasePath, tokenEndpoint,
		builder.Configuration[ "Scheduling:ClientId" ]!, builder.Configuration[ "Scheduling:ClientSecret" ]!,
		builder.Configuration[ "Scheduling:Scope" ]! ) );

// Handlers for every job type a group here may claim (scheduling-job-handler).
builder.Services.AddScheduledJobHandler<ReportJobHandler>( "report" );
builder.Services.AddScheduledJobHandler<CleanupJobHandler>( "cleanup" );

// Workers 1-2: cleanup only, with the default coordinator. A claimant set explicitly, so a restarted
// instance takes over its own runs; distinct from every other live process.
builder.Services.AddSchedulingWorkers( new WorkerOptions { Claimant = "jobs-a-cleanup", Workers = 2, JobType = "cleanup" } );

// Workers 3-6: reports only, under their own claimant and credentials.
builder.Services.AddSchedulingWorkers(
		new WorkerOptions
		{
			Claimant = "jobs-a-reports",
			Workers = 4,
			JobType = "report",
			HeartbeatInterval = TimeSpan.FromSeconds( 30 ),   // well under the scheduler's LeaseDuration (default 2 min)
			CancellationGrace = TimeSpan.FromSeconds( 10 ),
			AbandonTimeout = TimeSpan.FromSeconds( 5 )
		},
		p => Coordinator(
				p, schedulerUrl, coordinatorBasePath, tokenEndpoint,
				builder.Configuration[ "Scheduling:Reports:ClientId" ]!, builder.Configuration[ "Scheduling:Reports:ClientSecret" ]!,
				builder.Configuration[ "Scheduling:Scope" ]! ) );

// Stop the six workers together, so each gets its CancellationGrace + AbandonTimeout (15 s) within the
// shutdown timeout, and none keeps claiming while the others stop.
builder.Services.Configure<HostOptions>( o =>
{
	o.ServicesStopConcurrently = true;
	o.ShutdownTimeout = TimeSpan.FromSeconds( 30 );
} );

builder.Build().Run();
