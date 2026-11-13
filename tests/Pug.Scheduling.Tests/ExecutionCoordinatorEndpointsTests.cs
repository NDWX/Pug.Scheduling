using System.Net;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi.Models;
using Pug.Scheduling.Tests.Infrastructure;


namespace Pug.Scheduling.Tests;

/// <summary>
/// Endpoint tests for the transport an externally hosted worker binds
/// <see cref="IExecutionCoordinator"/> to: the claim → renew → report cycle over HTTP, and the
/// trust boundary — every operation requires a grant on the claimant identity being acted as, so
/// one worker's credentials cannot claim or complete runs as another.
/// </summary>
public class ExecutionCoordinatorEndpointsTests : IClassFixture<SchedulingApiFixture>
{
	private const string Owner = "owner-1";
	private const string Worker = "remote-worker-1";
	private const string OtherWorker = "remote-worker-2";

	private static int _lastWorkerNumber;

	private readonly SchedulingApiFixture _fixture;

	// The fixture's database outlives each test, and a worker re-takes its own live leases, so
	// every test claims as a worker number of its own: claims another test left live are never
	// handed back to it.
	private readonly int _worker = Interlocked.Increment( ref _lastWorkerNumber );

	public ExecutionCoordinatorEndpointsTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;

		foreach( string operation in new[]
				{
					CoordinatorSecurityOperations.ClaimJobs, CoordinatorSecurityOperations.RenewClaim,
					CoordinatorSecurityOperations.ReportCompletion
				} )
			// Grants are scoped to the claimant identity the caller may act as.
			_fixture.Authorizations.Grant(
					Worker, operation, SecurityObjectTypes.ExecutionCoordinator, identifier: Worker );
	}

	// Registers a schedule and requests an immediate run, so a claim finds it due — all through
	// the HTTP surface, as a remote worker would see it.
	private async Task<string> RegisterDueScheduleAsync( string name )
	{
		using HttpClient client = _fixture.CreateClient( Owner );

		ScheduleDefinition definition = new()
		{
			Domain = SchedulingApiFixture.Domain,
			Name = name,
			JobType = SchedulingApiFixture.JobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

		HttpResponseMessage registration =
			await client.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( definition ) );

		Assert.Equal( HttpStatusCode.Created, registration.StatusCode );

		string identifier = await SchedulingApiFixture.ReadAsync<string>( registration );

		Assert.Equal(
				HttpStatusCode.Accepted,
				( await client.PostAsync( $"/schedules/{identifier}/run", null ) ).StatusCode );

		return identifier;
	}

	private static Task<HttpResponseMessage> PostClaimAsync( HttpClient client, JobRunClaimRequest request ) =>
		client.PostAsync( "/executionCoordinator/JobRunClaims", SchedulingApiFixture.JsonContent( request ) );

	private async Task<ClaimedJob> ClaimAsync( HttpClient client, string claimant )
	{
		HttpResponseMessage response =
			await PostClaimAsync( client, new JobRunClaimRequest { Claimant = claimant, Worker = _worker } );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		return await SchedulingApiFixture.ReadAsync<ClaimedJob>( response );
	}

	private string CallerQuery( string claimant, int? worker = null ) =>
		$"claimant={claimant}&worker={worker ?? _worker}";

	[Fact]
	public async Task UnauthenticatedRequestIsRejected()
	{
		using HttpClient client = _fixture.CreateClient( null );

		HttpResponseMessage response =
			await PostClaimAsync( client, new JobRunClaimRequest { Claimant = Worker, Worker = _worker } );

		Assert.Equal( HttpStatusCode.Unauthorized, response.StatusCode );
	}

	[Fact]
	public async Task ClaimingAsAnotherWorkerIsForbidden()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		// The caller is granted for its own claimant identity only.
		HttpResponseMessage response =
			await PostClaimAsync( client, new JobRunClaimRequest { Claimant = OtherWorker, Worker = _worker } );

		Assert.Equal( HttpStatusCode.Forbidden, response.StatusCode );
	}

	[Theory]
	// An omitted worker number deserializes as 0 and is refused like any other below 1.
	[InlineData( Worker, 0, "worker" )]
	[InlineData( Worker, -1, "worker" )]
	// '/' separates claimant from worker in the lease holder, so a claimant may not carry one.
	[InlineData( "remote-worker-1/2", 1, "claimant" )]
	public async Task ClaimRequestIsValidated( string claimant, int worker, string argument )
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response =
			await PostClaimAsync( client, new JobRunClaimRequest { Claimant = claimant, Worker = worker } );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		// The rejection comes from the coordinator itself — the endpoint reports its parameter
		// name, not the request property's.
		Assert.Equal( "INVALID_ARGUMENT", error.Type );
		Assert.Equal( argument, error.RequestArgument );
	}

	[Fact]
	public async Task NothingDueAnswersNoContent()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		// A job type nothing is scheduled under: due work other tests left behind cannot answer.
		HttpResponseMessage response = await PostClaimAsync(
				client, new JobRunClaimRequest { Claimant = Worker, Worker = _worker, JobType = "no-such-type" } );

		Assert.Equal( HttpStatusCode.NoContent, response.StatusCode );
		Assert.Empty( await response.Content.ReadAsByteArrayAsync() );
	}

	[Fact]
	public async Task ClaimHonorsTheJobTypeFilter()
	{
		string identifier = await RegisterDueScheduleAsync( "Filtered schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await PostClaimAsync(
				client,
				new JobRunClaimRequest { Claimant = Worker, Worker = _worker, JobType = SchedulingApiFixture.JobType } );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		ClaimedJob job = await SchedulingApiFixture.ReadAsync<ClaimedJob>( response );

		Assert.Equal( identifier, job.Context.Schedule );
		Assert.Equal( SchedulingApiFixture.JobType, job.Context.JobType );
	}

	[Fact]
	public async Task ClaimCarriesTheExecutionContextAWorkerNeeds()
	{
		string identifier = await RegisterDueScheduleAsync( "Claimed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = await ClaimAsync( client, Worker );

		Assert.Equal( identifier, job.Context.Schedule );
		Assert.Equal( SchedulingApiFixture.Domain, job.Context.Domain );
		Assert.Equal( SchedulingApiFixture.JobType, job.Context.JobType );
		Assert.Equal( Owner, job.Context.Owner.Identifier );

		// Run-now raised the manual trigger, and the claim carries the parameters the worker
		// enforces locally.
		Assert.Equal( RunTriggers.Manual, job.Context.Trigger );
		Assert.Equal( TimeSpan.FromMinutes( 10 ), job.Timeout );
		Assert.True( job.ClaimExpiry > DateTime.UtcNow );
	}

	[Fact]
	public async Task ClaimIsExclusiveAcrossWorkers()
	{
		string identifier = await RegisterDueScheduleAsync( "Contested schedule" );

		_fixture.Authorizations.Grant(
				OtherWorker, CoordinatorSecurityOperations.ClaimJobs, SecurityObjectTypes.ExecutionCoordinator,
				identifier: OtherWorker );

		using HttpClient first = _fixture.CreateClient( Worker );
		using HttpClient second = _fixture.CreateClient( OtherWorker );

		Assert.Equal( identifier, ( await ClaimAsync( first, Worker ) ).Context.Schedule );

		// Everything due is leased to other workers by now, this test's claim included.
		HttpResponseMessage response =
			await PostClaimAsync( second, new JobRunClaimRequest { Claimant = OtherWorker, Worker = _worker } );

		Assert.Equal( HttpStatusCode.NoContent, response.StatusCode );
	}

	[Fact]
	public async Task RenewalReportsWhetherTheClaimIsStillHeld()
	{
		string identifier = await RegisterDueScheduleAsync( "Renewed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = await ClaimAsync( client, Worker );

		Assert.Equal( identifier, job.Context.Schedule );

		HttpResponseMessage renewal = await client.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/leaseRenewal?{CallerQuery( Worker )}", null );

		Assert.Equal( HttpStatusCode.OK, renewal.StatusCode );
		Assert.True( await SchedulingApiFixture.ReadAsync<bool>( renewal ) );

		// The claim is held by the worker, not by its group: a sibling worker cannot renew it.
		HttpResponseMessage siblingRenewal = await client.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/leaseRenewal?{CallerQuery( Worker, _worker + 1000 )}",
				null );

		Assert.Equal( HttpStatusCode.OK, siblingRenewal.StatusCode );
		Assert.False( await SchedulingApiFixture.ReadAsync<bool>( siblingRenewal ) );

		// A worker that does not hold the claim is told so rather than given an error status.
		_fixture.Authorizations.Grant(
				OtherWorker, CoordinatorSecurityOperations.RenewClaim, SecurityObjectTypes.ExecutionCoordinator,
				identifier: OtherWorker );

		using HttpClient other = _fixture.CreateClient( OtherWorker );

		HttpResponseMessage foreignRenewal = await other.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/leaseRenewal?{CallerQuery( OtherWorker )}", null );

		Assert.Equal( HttpStatusCode.OK, foreignRenewal.StatusCode );
		Assert.False( await SchedulingApiFixture.ReadAsync<bool>( foreignRenewal ) );
	}

	[Theory]
	[InlineData( "leaseRenewal", "", "MISSING_ARGUMENT", "claimant" )]
	[InlineData( "leaseRenewal", "?worker=1", "MISSING_ARGUMENT", "claimant" )]
	[InlineData( "leaseRenewal", "?claimant=remote-worker-1", "MISSING_ARGUMENT", "worker" )]
	[InlineData( "leaseRenewal", "?claimant=remote-worker-1&worker=first", "INVALID_ARGUMENT", "worker" )]
	[InlineData( "leaseRenewal", "?claimant=remote-worker-1&worker=0", "INVALID_ARGUMENT", "worker" )]
	[InlineData( "leaseRenewal", "?claimant=remote-worker-1/2&worker=1", "INVALID_ARGUMENT", "claimant" )]
	[InlineData( "completion", "?claimant=remote-worker-1", "MISSING_ARGUMENT", "worker" )]
	[InlineData( "completion", "?claimant=remote-worker-1&worker=-1", "INVALID_ARGUMENT", "worker" )]
	[InlineData( "completion", "?claimant=remote-worker-1&worker=0", "INVALID_ARGUMENT", "worker" )]
	public async Task RunOperationsRequireWorkerIdentity( string action, string query, string type, string argument )
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/some-run/{action}{query}",
				SchedulingApiFixture.JsonContent(
						new JobRunCompletion { Result = RunResults.Succeeded, Outcome = null } ) );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( type, error.Type );
		Assert.Equal( argument, error.RequestArgument );
	}

	[Fact]
	public async Task ReportedCompletionIsRecordedAndReleasesTheClaim()
	{
		string identifier = await RegisterDueScheduleAsync( "Completed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = await ClaimAsync( client, Worker );

		Assert.Equal( identifier, job.Context.Schedule );

		JobRunCompletion completion = new()
		{
			Result = RunResults.Succeeded,
			Outcome = new JobOutcome { Successful = true, Details = /*lang=json*/ """{"output":"report-1.pdf"}""" }
		};

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/completion?{CallerQuery( Worker )}",
				SchedulingApiFixture.JsonContent( completion ) );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		using HttpClient ownerClient = _fixture.CreateClient( Owner );

		IEnumerable<ScheduleRun> runs = await SchedulingApiFixture.ReadAsync<IEnumerable<ScheduleRun>>(
				await ownerClient.GetAsync( $"/schedules/{identifier}/runs" ) );

		ScheduleRun run = Assert.Single( runs );

		Assert.Equal( RunResults.Succeeded, run.Result );
		Assert.True( run.Outcome.Successful );
		Assert.NotNull( run.Finished );

		// The claim was released and the schedule advanced to its next occurrence.
		ScheduleInfo schedule = await SchedulingApiFixture.ReadAsync<ScheduleInfo>(
				await ownerClient.GetAsync( $"/schedules/{identifier}" ) );

		Assert.Null( schedule.ExecutionLease );
		Assert.False( schedule.State.RunRequested );
		Assert.Equal( 0, schedule.State.Attempt );
	}

	[Fact]
	public async Task CompletionOfUnknownRunIsNotFound()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/no-such-run/completion?{CallerQuery( Worker )}",
				SchedulingApiFixture.JsonContent(
						new JobRunCompletion { Result = RunResults.Failed, Outcome = null } ) );

		Assert.Equal( HttpStatusCode.NotFound, response.StatusCode );
	}

	[Fact]
	public async Task CompletionAsAnotherWorkerIsForbidden()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/some-run/completion?{CallerQuery( OtherWorker )}",
				SchedulingApiFixture.JsonContent(
						new JobRunCompletion { Result = RunResults.Succeeded, Outcome = null } ) );

		Assert.Equal( HttpStatusCode.Forbidden, response.StatusCode );
	}
}
