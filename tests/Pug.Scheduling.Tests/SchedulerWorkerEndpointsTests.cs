using System.Net;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.Tests.Infrastructure;
using Pug.Scheduling.Worker.WebApi;

namespace Pug.Scheduling.Tests;

/// <summary>
/// Worker management endpoints have no owner exemption: every operation requires an explicit
/// grant on the worker's claimant identity, so an ordinary user is denied even for their own
/// schedules' worker.
/// </summary>
public class SchedulerWorkerEndpointsTests : IClassFixture<SchedulingApiFixture>
{
	private const string Operator = "operator-1";
	private const string OrdinaryUser = "user-1";

	private readonly SchedulingApiFixture _fixture;

	public SchedulerWorkerEndpointsTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;
	}

	// Worker grants are qualified by the claimant identity of the worker being managed.
	private void GrantOperator( string operation ) =>
		_fixture.Authorizations.Grant(
				Operator, operation, SecurityObjectTypes.SchedulerWorker,
				identifier: FakeSchedulerWorker.Claimant );

	[Fact]
	public async Task UnauthenticatedRequestIsRejected()
	{
		using HttpClient client = _fixture.CreateClient( null );

		Assert.Equal( HttpStatusCode.Unauthorized, ( await client.GetAsync( "/worker" ) ).StatusCode );
	}

	[Fact]
	public async Task StatusRequiresGrant()
	{
		using HttpClient denied = _fixture.CreateClient( OrdinaryUser );

		Assert.Equal( HttpStatusCode.Forbidden, ( await denied.GetAsync( "/worker" ) ).StatusCode );

		GrantOperator( WorkerSecurityOperations.GetInfo );

		using HttpClient granted = _fixture.CreateClient( Operator );

		HttpResponseMessage response = await granted.GetAsync( "/worker" );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		WorkerInfo info = await SchedulingApiFixture.ReadAsync<WorkerInfo>( response );

		Assert.Equal( FakeSchedulerWorker.Claimant, info.Claimant );
		Assert.Equal( 0, info.ActiveJobs );
	}

	[Fact]
	public async Task PauseAndResumeRequireGrantsAndReportResultingState()
	{
		using HttpClient denied = _fixture.CreateClient( OrdinaryUser );

		Assert.Equal(
				HttpStatusCode.Forbidden, ( await denied.PostAsync( "/worker/pause", null ) ).StatusCode );

		Assert.Equal(
				HttpStatusCode.Forbidden, ( await denied.PostAsync( "/worker/resume", null ) ).StatusCode );

		// The denied attempts must not have changed the worker.
		Assert.False( _fixture.Worker.GetInfo().Paused );

		GrantOperator( WorkerSecurityOperations.Pause );
		GrantOperator( WorkerSecurityOperations.Resume );

		using HttpClient client = _fixture.CreateClient( Operator );

		HttpResponseMessage paused = await client.PostAsync( "/worker/pause", null );

		Assert.Equal( HttpStatusCode.OK, paused.StatusCode );
		Assert.True( ( await SchedulingApiFixture.ReadAsync<WorkerInfo>( paused ) ).Paused );

		HttpResponseMessage resumed = await client.PostAsync( "/worker/resume", null );

		Assert.Equal( HttpStatusCode.OK, resumed.StatusCode );
		Assert.False( ( await SchedulingApiFixture.ReadAsync<WorkerInfo>( resumed ) ).Paused );
	}
}
