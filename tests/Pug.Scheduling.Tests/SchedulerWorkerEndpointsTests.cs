using System.Net;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker.WebApi;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Worker management endpoints have no owner exemption: every operation requires an explicit
/// grant on the claimant identity of the worker addressed, so an ordinary user is denied even for
/// their own schedules' worker. The fixture hosts two fake workers under different claimants —
/// worker 1 (<see cref="SchedulingApiFixture.WorkerClaimant"/>) and worker 2
/// (<see cref="SchedulingApiFixture.OtherWorkerClaimant"/>, job type
/// <see cref="SchedulingApiFixture.JobType"/>). Grants accumulate across the class's tests, so
/// each test acts as users of its own.
/// </summary>
public class SchedulerWorkerEndpointsTests : IClassFixture<SchedulingApiFixture>
{
	private const string OrdinaryUser = "user-1";

	private readonly SchedulingApiFixture _fixture;

	public SchedulerWorkerEndpointsTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;
	}

	// Worker grants are qualified by the claimant identity of the worker being managed.
	private void Grant( string user, string operation, string claimant ) =>
		_fixture.Authorizations.Grant(
				user, operation, SecurityObjectTypes.SchedulerWorker, identifier: claimant );

	[Theory]
	[InlineData( "GET", "/workers" )]
	[InlineData( "GET", "/workers/1" )]
	[InlineData( "POST", "/workers/1/pause" )]
	[InlineData( "POST", "/workers/1/resume" )]
	public async Task UnauthenticatedRequestIsRejected( string method, string path )
	{
		using HttpClient client = _fixture.CreateClient( null );

		HttpResponseMessage response = await client.SendAsync( new HttpRequestMessage( new HttpMethod( method ), path ) );

		Assert.Equal( HttpStatusCode.Unauthorized, response.StatusCode );
	}

	[Fact]
	public async Task ListContainsOnlyWorkersWhoseClaimantTheCallerMayView()
	{
		// No grant at all: an empty list, not a 403.
		using HttpClient ungranted = _fixture.CreateClient( OrdinaryUser );

		HttpResponseMessage response = await ungranted.GetAsync( "/workers" );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );
		Assert.Empty( await SchedulingApiFixture.ReadAsync<WorkerInfo[]>( response ) );

		// A grant on one claimant shows that claimant's worker only.
		const string partial = "list-viewer-partial";

		Grant( partial, WorkerSecurityOperations.GetInfo, SchedulingApiFixture.OtherWorkerClaimant );

		using HttpClient partialClient = _fixture.CreateClient( partial );

		WorkerInfo visible = Assert.Single(
				await SchedulingApiFixture.ReadAsync<WorkerInfo[]>( await partialClient.GetAsync( "/workers" ) ) );

		Assert.Equal( 2, visible.Worker );
		Assert.Equal( SchedulingApiFixture.OtherWorkerClaimant, visible.Claimant );
		Assert.Equal( SchedulingApiFixture.JobType, visible.JobType );

		// A grant other than GetInfo does not make a worker visible.
		const string pauser = "list-viewer-pauser";

		Grant( pauser, WorkerSecurityOperations.Pause, SchedulingApiFixture.WorkerClaimant );

		using HttpClient pauserClient = _fixture.CreateClient( pauser );

		Assert.Empty( await SchedulingApiFixture.ReadAsync<WorkerInfo[]>( await pauserClient.GetAsync( "/workers" ) ) );

		// Grants on both claimants show both, in worker-number order.
		const string full = "list-viewer-full";

		Grant( full, WorkerSecurityOperations.GetInfo, SchedulingApiFixture.WorkerClaimant );
		Grant( full, WorkerSecurityOperations.GetInfo, SchedulingApiFixture.OtherWorkerClaimant );

		using HttpClient fullClient = _fixture.CreateClient( full );

		WorkerInfo[] all = await SchedulingApiFixture.ReadAsync<WorkerInfo[]>( await fullClient.GetAsync( "/workers" ) );

		Assert.Equal( new[] { 1, 2 }, all.Select( info => info.Worker ) );
		Assert.Equal( SchedulingApiFixture.WorkerClaimant, all[0].Claimant );
		Assert.Null( all[0].JobType );
	}

	[Fact]
	public async Task StatusRequiresGrantOnTheWorkersClaimant()
	{
		const string viewer = "status-viewer";

		using HttpClient denied = _fixture.CreateClient( OrdinaryUser );

		Assert.Equal( HttpStatusCode.Forbidden, ( await denied.GetAsync( "/workers/1" ) ).StatusCode );

		Grant( viewer, WorkerSecurityOperations.GetInfo, SchedulingApiFixture.WorkerClaimant );

		using HttpClient granted = _fixture.CreateClient( viewer );

		HttpResponseMessage response = await granted.GetAsync( "/workers/1" );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		WorkerInfo info = await SchedulingApiFixture.ReadAsync<WorkerInfo>( response );

		Assert.Equal( SchedulingApiFixture.WorkerClaimant, info.Claimant );
		Assert.Equal( 1, info.Worker );
		Assert.Equal( 0, info.ActiveJobs );

		// The grant is the claimant's: it does not reach worker 2, which claims as another.
		Assert.Equal( HttpStatusCode.Forbidden, ( await granted.GetAsync( "/workers/2" ) ).StatusCode );
	}

	[Theory]
	[InlineData( "GET", "/workers/99" )]
	[InlineData( "POST", "/workers/99/pause" )]
	[InlineData( "POST", "/workers/99/resume" )]
	[InlineData( "GET", "/workers/first" )]
	public async Task UnknownWorkerIsNotFound( string method, string path )
	{
		const string administrator = "unknown-worker-administrator";

		// Grants on every hosted claimant do not make an unknown number exist.
		foreach( string claimant in new[]
				{ SchedulingApiFixture.WorkerClaimant, SchedulingApiFixture.OtherWorkerClaimant } )
		foreach( string operation in new[]
				{ WorkerSecurityOperations.GetInfo, WorkerSecurityOperations.Pause, WorkerSecurityOperations.Resume } )
			Grant( administrator, operation, claimant );

		using HttpClient client = _fixture.CreateClient( administrator );

		HttpResponseMessage response = await client.SendAsync( new HttpRequestMessage( new HttpMethod( method ), path ) );

		Assert.Equal( HttpStatusCode.NotFound, response.StatusCode );
	}

	[Fact]
	public async Task PauseAndResumeRequireGrantsAndReportResultingState()
	{
		const string operatorUser = "pause-operator";

		using HttpClient denied = _fixture.CreateClient( OrdinaryUser );

		Assert.Equal(
				HttpStatusCode.Forbidden, ( await denied.PostAsync( "/workers/1/pause", null ) ).StatusCode );

		Assert.Equal(
				HttpStatusCode.Forbidden, ( await denied.PostAsync( "/workers/1/resume", null ) ).StatusCode );

		// The denied attempts must not have changed the worker.
		Assert.False( _fixture.Worker.GetInfo().Paused );

		Grant( operatorUser, WorkerSecurityOperations.Pause, SchedulingApiFixture.WorkerClaimant );
		Grant( operatorUser, WorkerSecurityOperations.Resume, SchedulingApiFixture.WorkerClaimant );

		using HttpClient client = _fixture.CreateClient( operatorUser );

		HttpResponseMessage paused = await client.PostAsync( "/workers/1/pause", null );

		Assert.Equal( HttpStatusCode.OK, paused.StatusCode );

		WorkerInfo pausedInfo = await SchedulingApiFixture.ReadAsync<WorkerInfo>( paused );

		Assert.True( pausedInfo.Paused );
		Assert.Equal( 1, pausedInfo.Worker );

		HttpResponseMessage resumed = await client.PostAsync( "/workers/1/resume", null );

		Assert.Equal( HttpStatusCode.OK, resumed.StatusCode );
		Assert.False( ( await SchedulingApiFixture.ReadAsync<WorkerInfo>( resumed ) ).Paused );

		// Grants on worker 1's claimant do not reach worker 2.
		Assert.Equal(
				HttpStatusCode.Forbidden, ( await client.PostAsync( "/workers/2/pause", null ) ).StatusCode );

		Assert.False( _fixture.OtherWorker.GetInfo().Paused );
	}

	[Fact]
	public async Task PausingOneWorkerLeavesTheOtherRunning()
	{
		const string operatorUser = "group-operator";

		foreach( string claimant in new[]
				{ SchedulingApiFixture.WorkerClaimant, SchedulingApiFixture.OtherWorkerClaimant } )
		{
			Grant( operatorUser, WorkerSecurityOperations.Pause, claimant );
			Grant( operatorUser, WorkerSecurityOperations.Resume, claimant );
		}

		using HttpClient client = _fixture.CreateClient( operatorUser );

		HttpResponseMessage paused = await client.PostAsync( "/workers/2/pause", null );

		Assert.Equal( HttpStatusCode.OK, paused.StatusCode );
		Assert.Equal( 2, ( await SchedulingApiFixture.ReadAsync<WorkerInfo>( paused ) ).Worker );

		Assert.True( _fixture.OtherWorker.GetInfo().Paused );
		Assert.False( _fixture.Worker.GetInfo().Paused );

		HttpResponseMessage resumed = await client.PostAsync( "/workers/2/resume", null );

		Assert.Equal( HttpStatusCode.OK, resumed.StatusCode );

		Assert.False( _fixture.OtherWorker.GetInfo().Paused );
		Assert.False( _fixture.Worker.GetInfo().Paused );
	}
}
