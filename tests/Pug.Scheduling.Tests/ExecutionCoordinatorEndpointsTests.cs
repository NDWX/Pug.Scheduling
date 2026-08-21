using System.Net;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.WebApi;

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

	private readonly SchedulingApiFixture _fixture;

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

	private async Task<IEnumerable<ClaimedJob>> ClaimAsync( HttpClient client, string claimant, int maximumJobs = 10 )
	{
		HttpResponseMessage response = await client.PostAsync(
				"/executionCoordinator/JobRunClaims",
				SchedulingApiFixture.JsonContent(
						new JobRunClaimRequest { Claimant = claimant, MaximumJobs = maximumJobs } ) );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		return await SchedulingApiFixture.ReadAsync<IEnumerable<ClaimedJob>>( response );
	}

	[Fact]
	public async Task UnauthenticatedRequestIsRejected()
	{
		using HttpClient client = _fixture.CreateClient( null );

		HttpResponseMessage response = await client.PostAsync(
				"/executionCoordinator/JobRunClaims",
				SchedulingApiFixture.JsonContent( new JobRunClaimRequest { Claimant = Worker, MaximumJobs = 1 } ) );

		Assert.Equal( HttpStatusCode.Unauthorized, response.StatusCode );
	}

	[Fact]
	public async Task ClaimingAsAnotherWorkerIsForbidden()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		// The caller is granted for its own claimant identity only.
		HttpResponseMessage response = await client.PostAsync(
				"/executionCoordinator/JobRunClaims",
				SchedulingApiFixture.JsonContent(
						new JobRunClaimRequest { Claimant = OtherWorker, MaximumJobs = 1 } ) );

		Assert.Equal( HttpStatusCode.Forbidden, response.StatusCode );
	}

	[Fact]
	public async Task ClaimRequestIsValidated()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await client.PostAsync(
				"/executionCoordinator/JobRunClaims",
				SchedulingApiFixture.JsonContent( new JobRunClaimRequest { Claimant = Worker, MaximumJobs = 0 } ) );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		// The rejection comes from the coordinator itself — the endpoint reports its parameter
		// name, not the request property's.
		Assert.Equal( "INVALID_ARGUMENT", error.Type );
		Assert.Equal( "maximumJobs", error.RequestArgument );
	}

	[Fact]
	public async Task ClaimCarriesTheExecutionContextAWorkerNeeds()
	{
		string identifier = await RegisterDueScheduleAsync( "Claimed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = Assert.Single(
				await ClaimAsync( client, Worker ), claimed => claimed.Context.Schedule == identifier );

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
		await RegisterDueScheduleAsync( "Contested schedule" );

		_fixture.Authorizations.Grant(
				OtherWorker, CoordinatorSecurityOperations.ClaimJobs, SecurityObjectTypes.ExecutionCoordinator,
				identifier: OtherWorker );

		using HttpClient first = _fixture.CreateClient( Worker );
		using HttpClient second = _fixture.CreateClient( OtherWorker );

		IEnumerable<ClaimedJob> claimed = await ClaimAsync( first, Worker );

		Assert.NotEmpty( claimed );

		// Everything due was just claimed and leased to the first worker.
		Assert.Empty( await ClaimAsync( second, OtherWorker ) );
	}

	[Fact]
	public async Task RenewalReportsWhetherTheClaimIsStillHeld()
	{
		await RegisterDueScheduleAsync( "Renewed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = ( await ClaimAsync( client, Worker ) ).First();

		HttpResponseMessage renewal = await client.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/leaseRenewal?worker={Worker}", null );

		Assert.Equal( HttpStatusCode.OK, renewal.StatusCode );
		Assert.True( await SchedulingApiFixture.ReadAsync<bool>( renewal ) );

		// A worker that does not hold the claim is told so rather than given an error status.
		_fixture.Authorizations.Grant(
				OtherWorker, CoordinatorSecurityOperations.RenewClaim, SecurityObjectTypes.ExecutionCoordinator,
				identifier: OtherWorker );

		using HttpClient other = _fixture.CreateClient( OtherWorker );

		HttpResponseMessage foreignRenewal = await other.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/leaseRenewal?worker={OtherWorker}", null );

		Assert.Equal( HttpStatusCode.OK, foreignRenewal.StatusCode );
		Assert.False( await SchedulingApiFixture.ReadAsync<bool>( foreignRenewal ) );
	}

	[Fact]
	public async Task RenewalRequiresWorkerIdentity()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response =
			await client.PostAsync( "/executionCoordinator/JobRuns/some-run/leaseRenewal", null );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( "MISSING_ARGUMENT", error.Type );
		Assert.Equal( "worker", error.RequestArgument );
	}

	[Fact]
	public async Task ReportedCompletionIsRecordedAndReleasesTheClaim()
	{
		string identifier = await RegisterDueScheduleAsync( "Completed schedule" );

		using HttpClient client = _fixture.CreateClient( Worker );

		ClaimedJob job = Assert.Single(
				await ClaimAsync( client, Worker ), claimed => claimed.Context.Schedule == identifier );

		JobRunCompletion completion = new()
		{
			Result = RunResults.Succeeded,
			Outcome = new JobOutcome { Successful = true, Details = /*lang=json*/ """{"output":"report-1.pdf"}""" }
		};

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/{job.Context.Run}/completion?worker={Worker}",
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
				$"/executionCoordinator/JobRuns/no-such-run/completion?worker={Worker}",
				SchedulingApiFixture.JsonContent(
						new JobRunCompletion { Result = RunResults.Failed, Outcome = null } ) );

		Assert.Equal( HttpStatusCode.NotFound, response.StatusCode );
	}

	[Fact]
	public async Task CompletionAsAnotherWorkerIsForbidden()
	{
		using HttpClient client = _fixture.CreateClient( Worker );

		HttpResponseMessage response = await client.PostAsync(
				$"/executionCoordinator/JobRuns/some-run/completion?worker={OtherWorker}",
				SchedulingApiFixture.JsonContent(
						new JobRunCompletion { Result = RunResults.Succeeded, Outcome = null } ) );

		Assert.Equal( HttpStatusCode.Forbidden, response.StatusCode );
	}
}
