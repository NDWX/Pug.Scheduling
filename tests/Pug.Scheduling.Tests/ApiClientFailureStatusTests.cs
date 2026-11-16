using System.Net;
using Pug.HttpApiClient;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Client;
using Pug.Scheduling.Models;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Pug.HttpApiClient 0.6.0 hands failure statuses it does not list (503, 504, 429, 408) back as a
/// response. Every client call must still surface them as a failure — above all a completion
/// report, which a worker would otherwise believe recorded (D29). Served by a stub transport, since
/// the real endpoints never answer these.
/// </summary>
public class ApiClientFailureStatusTests
{
	// Answers every request with the one status.
	private sealed class StatusHandler( HttpStatusCode status ) : HttpMessageHandler
	{
		protected override Task<HttpResponseMessage> SendAsync( HttpRequestMessage request,
																CancellationToken cancellationToken ) =>
			Task.FromResult( new HttpResponseMessage( status ) { Content = new StringContent( "unavailable" ) } );
	}

	private sealed class StatusClientFactory( HttpStatusCode status ) : IHttpClientFactory
	{
		public HttpClient CreateClient( string name ) => new( new StatusHandler( status ) );
	}

	private static IHttpApiClient ApiClient( HttpStatusCode status ) =>
		new HttpApiClient.HttpApiClient( new Uri( "http://localhost/" ), new StatusClientFactory( status ) );

	private static async Task AssertFailsWith( HttpStatusCode status, Func<Task> call )
	{
		HttpApiRequestException exception = await Assert.ThrowsAsync<HttpApiRequestException>( call );

		Assert.Equal( status, exception.ResponseStatusCode );
	}

	[Theory]
	[InlineData( HttpStatusCode.ServiceUnavailable )]
	[InlineData( HttpStatusCode.GatewayTimeout )]
	[InlineData( HttpStatusCode.TooManyRequests )]
	[InlineData( HttpStatusCode.RequestTimeout )]
	public async Task CoordinatorCallsFailOnUnlistedStatuses( HttpStatusCode status )
	{
		ExecutionCoordinatorClient client = new( ApiClient( status ) );

		await AssertFailsWith(
				status,
				() => client.ReportCompletionAsync(
						"run-1", "claimant", 1, RunResults.Succeeded, new JobOutcome { Successful = true } ) );

		await AssertFailsWith( status, () => client.ClaimDueJobAsync( "claimant", 1, null ) );
		await AssertFailsWith( status, () => client.RenewClaimAsync( "run-1", "claimant", 1 ) );
		await AssertFailsWith( status, () => client.AbandonClaimAsync( "run-1", "claimant", 1 ) );
	}

	[Theory]
	[InlineData( HttpStatusCode.ServiceUnavailable )]
	[InlineData( HttpStatusCode.TooManyRequests )]
	public async Task SchedulerCallsFailOnUnlistedStatuses( HttpStatusCode status )
	{
		IScheduler client = new SchedulerClient( ApiClient( status ) );

		ScheduleDefinition definition = new()
		{
			Domain = "tenant-1",
			Name = "Unavailable",
			JobType = "test",
			Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

		// Typed calls: Pug.HttpApiClient's JSON helpers check the status themselves.
		await AssertFailsWith( status, () => client.GetScheduleAsync( "schedule-1" ) );
		await AssertFailsWith( status, () => client.RegisterAsync( definition ) );

		// Raw-response calls: checked by ApiFailures.EnsureSuccess.
		await AssertFailsWith( status, () => client.PauseAsync( "schedule-1" ) );
		await AssertFailsWith( status, () => client.ResumeAsync( "schedule-1" ) );
		await AssertFailsWith( status, () => client.RunNowAsync( "schedule-1" ) );
		await AssertFailsWith( status, () => client.DeleteAsync( "schedule-1" ) );

		await AssertFailsWith( status, () => client.UpdateDefinitionAsync( "schedule-1", definition ) );
	}
}
