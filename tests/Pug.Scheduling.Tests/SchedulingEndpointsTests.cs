using System.Net;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;
using Pug.Scheduling.WebApi.Models;
using Pug.Scheduling.Tests.Infrastructure;


namespace Pug.Scheduling.Tests;

/// <summary>
/// Endpoint tests over a TestServer host: HTTP contract (status codes, argument validation) and
/// the authorization matrix — owner (no grant needed), another user (denied), domain admin
/// (granted). Nothing is granted unless a test grants it, so every success by an owner proves
/// the owner rule survives the HTTP surface.
/// </summary>
public class SchedulingEndpointsTests : IClassFixture<SchedulingApiFixture>
{
	private const string Owner = "owner-1";
	private const string OtherUser = "other-1";
	private const string DomainAdmin = "admin-1";

	private readonly SchedulingApiFixture _fixture;

	public SchedulingEndpointsTests( SchedulingApiFixture fixture )
	{
		_fixture = fixture;
	}

	private static ScheduleDefinition NewDefinition( string name = "Test schedule", string? domain = null,
													Recurrence? recurrence = null ) =>
		new()
		{
			Domain = domain ?? SchedulingApiFixture.Domain,
			Name = name,
			JobType = SchedulingApiFixture.JobType,
			Payload = /*lang=json*/ """{"configuration":"report-1"}""",
			Recurrence = recurrence ?? new Recurrence { Interval = TimeSpan.FromHours( 1 ), TimeZone = "Etc/UTC" }
		};

	// Registers a schedule owned by the given user (owner rule: no grant required) and returns
	// its identifier.
	private async Task<string> RegisterAsync( string user, ScheduleDefinition? definition = null )
	{
		using HttpClient client = _fixture.CreateClient( user );

		HttpResponseMessage response = await client.PostAsync(
				"/schedules", SchedulingApiFixture.JsonContent( definition ?? NewDefinition() ) );

		Assert.Equal( HttpStatusCode.Created, response.StatusCode );

		return await SchedulingApiFixture.ReadAsync<string>( response );
	}

	[Fact]
	public async Task UnauthenticatedRequestIsRejected()
	{
		using HttpClient client = _fixture.CreateClient( null );

		HttpResponseMessage response = await client.GetAsync( $"/schedules?domain={SchedulingApiFixture.Domain}" );

		Assert.Equal( HttpStatusCode.Unauthorized, response.StatusCode );
	}

	[Fact]
	public async Task OwnerRegistersOwnScheduleWithoutGrant()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		ScheduleInfo schedule =
			await SchedulingApiFixture.ReadAsync<ScheduleInfo>( await client.GetAsync( $"/schedules/{identifier}" ) );

		Assert.Equal( Owner, schedule.Owner.Identifier );
		Assert.Equal( "USER", schedule.Owner.Type );
		Assert.Equal( SchedulingApiFixture.Domain, schedule.Definition.Domain );
		Assert.True( schedule.State.Enabled );
		Assert.NotNull( schedule.State.NextRun );
	}

	[Fact]
	public async Task RegisteringForAnotherPrincipalRequiresCreateGrant()
	{
		using HttpClient denied = _fixture.CreateClient( OtherUser );

		HttpResponseMessage response = await denied.PostAsync(
				$"/schedules?ownerType=USER&ownerIdentifier={Owner}",
				SchedulingApiFixture.JsonContent( NewDefinition() ) );

		Assert.Equal( HttpStatusCode.Forbidden, response.StatusCode );

		_fixture.Authorizations.Grant(
				DomainAdmin, SecurityOperations.Create, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		using HttpClient granted = _fixture.CreateClient( DomainAdmin );

		HttpResponseMessage grantedResponse = await granted.PostAsync(
				$"/schedules?ownerType=USER&ownerIdentifier={Owner}",
				SchedulingApiFixture.JsonContent( NewDefinition( "Registered on behalf" ) ) );

		Assert.Equal( HttpStatusCode.Created, grantedResponse.StatusCode );

		string identifier = await SchedulingApiFixture.ReadAsync<string>( grantedResponse );

		using HttpClient ownerClient = _fixture.CreateClient( Owner );

		ScheduleInfo schedule = await SchedulingApiFixture.ReadAsync<ScheduleInfo>(
				await ownerClient.GetAsync( $"/schedules/{identifier}" ) );

		Assert.Equal( Owner, schedule.Owner.Identifier );
	}

	[Fact]
	public async Task HalfSpecifiedOwnerIsRejected()
	{
		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage response = await client.PostAsync(
				$"/schedules?ownerIdentifier={Owner}", SchedulingApiFixture.JsonContent( NewDefinition() ) );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( "MISSING_ARGUMENT", error.Type );
		Assert.Equal( "ownerType", error.RequestArgument );
	}

	[Fact]
	public async Task InvalidDefinitionIsRejectedAsBadRequest()
	{
		using HttpClient client = _fixture.CreateClient( Owner );

		ScheduleDefinition definition = NewDefinition() with
		{
			Recurrence = new Recurrence { CronExpression = "not a cron expression", TimeZone = "Etc/UTC" }
		};

		HttpResponseMessage response =
			await client.PostAsync( "/schedules", SchedulingApiFixture.JsonContent( definition ) );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( "INVALID_ARGUMENT", error.Type );
	}

	[Fact]
	public async Task ListingRequiresDomain()
	{
		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage response = await client.GetAsync( "/schedules" );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( "MISSING_ARGUMENT", error.Type );
		Assert.Equal( "domain", error.RequestArgument );
	}

	[Fact]
	public async Task ListingOwnSchedulesNeedsNoGrantWhileBroaderListingDoes()
	{
		await RegisterAsync( Owner, NewDefinition( "Listed schedule" ) );

		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage own = await client.GetAsync(
				$"/schedules?domain={SchedulingApiFixture.Domain}&ownerType=USER&ownerIdentifier={Owner}" );

		Assert.Equal( HttpStatusCode.OK, own.StatusCode );

		IEnumerable<ScheduleInfo> schedules =
			await SchedulingApiFixture.ReadAsync<IEnumerable<ScheduleInfo>>( own );

		Assert.All( schedules, schedule => Assert.Equal( Owner, schedule.Owner.Identifier ) );
		Assert.Contains( schedules, schedule => schedule.Definition.Name == "Listed schedule" );

		// Listing the whole domain is not covered by the owner rule.
		HttpResponseMessage denied = await client.GetAsync( $"/schedules?domain={SchedulingApiFixture.Domain}" );

		Assert.Equal( HttpStatusCode.Forbidden, denied.StatusCode );

		_fixture.Authorizations.Grant(
				DomainAdmin, SecurityOperations.List, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		using HttpClient adminClient = _fixture.CreateClient( DomainAdmin );

		HttpResponseMessage granted = await adminClient.GetAsync( $"/schedules?domain={SchedulingApiFixture.Domain}" );

		Assert.Equal( HttpStatusCode.OK, granted.StatusCode );
		Assert.NotEmpty( await SchedulingApiFixture.ReadAsync<IEnumerable<ScheduleInfo>>( granted ) );
	}

	[Fact]
	public async Task ScheduleIsVisibleToOwnerAndDomainAdminOnly()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient otherClient = _fixture.CreateClient( OtherUser );

		Assert.Equal(
				HttpStatusCode.Forbidden, ( await otherClient.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );

		_fixture.Authorizations.Grant(
				DomainAdmin, SecurityOperations.GetInfo, SecurityObjectTypes.Schedule, SchedulingApiFixture.Domain );

		using HttpClient adminClient = _fixture.CreateClient( DomainAdmin );

		Assert.Equal( HttpStatusCode.OK, ( await adminClient.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );
	}

	[Fact]
	public async Task UnknownScheduleIsNotFound()
	{
		using HttpClient client = _fixture.CreateClient( Owner );

		Assert.Equal( HttpStatusCode.NotFound, ( await client.GetAsync( "/schedules/no-such-schedule" ) ).StatusCode );

		Assert.Equal(
				HttpStatusCode.NotFound,
				( await client.PostAsync( "/schedules/no-such-schedule/run", null ) ).StatusCode );

		Assert.Equal(
				HttpStatusCode.NotFound, ( await client.DeleteAsync( "/schedules/no-such-schedule" ) ).StatusCode );
	}

	[Fact]
	public async Task OwnerUpdatesDefinition()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		ScheduleDefinition updated = NewDefinition( "Renamed schedule" ) with
		{
			Recurrence = new Recurrence { Interval = TimeSpan.FromMinutes( 30 ), TimeZone = "Etc/UTC" }
		};

		HttpResponseMessage response = await client.PutAsync(
				$"/schedules/{identifier}", SchedulingApiFixture.JsonContent( updated ) );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );

		ScheduleInfo schedule =
			await SchedulingApiFixture.ReadAsync<ScheduleInfo>( await client.GetAsync( $"/schedules/{identifier}" ) );

		Assert.Equal( "Renamed schedule", schedule.Definition.Name );
		Assert.Equal( TimeSpan.FromMinutes( 30 ), schedule.Definition.Recurrence.Interval );

		// The update audit context records the caller.
		Assert.NotNull( schedule.LastUpdate );
		Assert.Equal( Owner, schedule.LastUpdate.Actor.Identifier );
	}

	[Fact]
	public async Task DomainCannotBeChangedThroughUpdate()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage response = await client.PutAsync(
				$"/schedules/{identifier}",
				SchedulingApiFixture.JsonContent( NewDefinition( domain: "tenant-2" ) ) );

		Assert.Equal( HttpStatusCode.BadRequest, response.StatusCode );

		BadRequestResultInfo error = await SchedulingApiFixture.ReadAsync<BadRequestResultInfo>( response );

		Assert.Equal( "INVALID_ARGUMENT", error.Type );
	}

	[Fact]
	public async Task UpdateAndDeleteByAnotherUserAreForbidden()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( OtherUser );

		HttpResponseMessage update = await client.PutAsync(
				$"/schedules/{identifier}", SchedulingApiFixture.JsonContent( NewDefinition( "Hijacked" ) ) );

		Assert.Equal( HttpStatusCode.Forbidden, update.StatusCode );

		Assert.Equal( HttpStatusCode.Forbidden, ( await client.DeleteAsync( $"/schedules/{identifier}" ) ).StatusCode );
	}

	[Fact]
	public async Task PauseAndResumeChangeScheduleState()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		Assert.Equal(
				HttpStatusCode.OK, ( await client.PostAsync( $"/schedules/{identifier}/pause", null ) ).StatusCode );

		ScheduleInfo paused =
			await SchedulingApiFixture.ReadAsync<ScheduleInfo>( await client.GetAsync( $"/schedules/{identifier}" ) );

		Assert.True( paused.State.Paused );

		Assert.Equal(
				HttpStatusCode.OK, ( await client.PostAsync( $"/schedules/{identifier}/resume", null ) ).StatusCode );

		ScheduleInfo resumed =
			await SchedulingApiFixture.ReadAsync<ScheduleInfo>( await client.GetAsync( $"/schedules/{identifier}" ) );

		Assert.False( resumed.State.Paused );
	}

	[Fact]
	public async Task RunNowIsAcceptedAndRaisesRunRequest()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage response = await client.PostAsync( $"/schedules/{identifier}/run", null );

		// Accepted, not Ok: the occurrence is only brought forward for a worker to claim.
		Assert.Equal( HttpStatusCode.Accepted, response.StatusCode );

		ScheduleInfo schedule =
			await SchedulingApiFixture.ReadAsync<ScheduleInfo>( await client.GetAsync( $"/schedules/{identifier}" ) );

		Assert.True( schedule.State.RunRequested );
	}

	[Fact]
	public async Task RunHistoryIsOwnerVisibleAndEmptyUntilExecuted()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		HttpResponseMessage response = await client.GetAsync( $"/schedules/{identifier}/runs" );

		Assert.Equal( HttpStatusCode.OK, response.StatusCode );
		Assert.Empty( await SchedulingApiFixture.ReadAsync<IEnumerable<ScheduleRun>>( response ) );

		using HttpClient otherClient = _fixture.CreateClient( OtherUser );

		Assert.Equal(
				HttpStatusCode.Forbidden,
				( await otherClient.GetAsync( $"/schedules/{identifier}/runs" ) ).StatusCode );
	}

	[Fact]
	public async Task OwnerDeletesOwnSchedule()
	{
		string identifier = await RegisterAsync( Owner );

		using HttpClient client = _fixture.CreateClient( Owner );

		Assert.Equal( HttpStatusCode.OK, ( await client.DeleteAsync( $"/schedules/{identifier}" ) ).StatusCode );
		Assert.Equal( HttpStatusCode.NotFound, ( await client.GetAsync( $"/schedules/{identifier}" ) ).StatusCode );
	}
}
