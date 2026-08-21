using Pug.HttpApiClient;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Feeds <see cref="HttpApiClient"/> clients bound to the TestServer, authenticated as a fixed
/// user — so the API clients are exercised against the real endpoint pipeline rather than a
/// stubbed transport.
/// </summary>
internal sealed class TestHttpClientFactory : IHttpClientFactory
{
	private readonly SchedulingApiFixture _fixture;
	private readonly string? _user;

	internal TestHttpClientFactory( SchedulingApiFixture fixture, string? user )
	{
		_fixture = fixture;
		_user = user;
	}

	public HttpClient CreateClient( string name ) => _fixture.CreateClient( _user );
}
