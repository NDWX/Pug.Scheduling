using Npgsql;
using Pug.Application.Data;
using Pug.Scheduling.Leases.Data.Abstractions;
using DapperData = Pug.Scheduling.Leases.Data.Postgres.DapperData;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Provisions a fresh, isolated PostgreSQL database for the execution-lease store (migrated from
/// the <see cref="DapperData"/> schema scripts) in the run's shared container — deliberately its
/// own database, mirroring the production separation of lease storage from the schedule store.
/// </summary>
public sealed class PostgresLeaseDatabaseFixture : ILeaseDatabaseFixture, IAsyncLifetime
{
	private string? _connectionString;

	public IApplicationData<IDataSession> ApplicationData { get; private set; } = null!;

	public async Task InitializeAsync()
	{
		_connectionString = await PostgresTestServer.CreateDatabaseAsync( "lease" );

		DapperData data = new( _connectionString, NpgsqlFactory.Instance );

		PostgresTestServer.UpgradeDatabase( _connectionString, data.GetSchemaUpgradeScripts() );

		ApplicationData = data;
	}

	public async Task DisposeAsync()
	{
		if( _connectionString is not null )
			await PostgresTestServer.DropDatabaseAsync( _connectionString );
	}
}
