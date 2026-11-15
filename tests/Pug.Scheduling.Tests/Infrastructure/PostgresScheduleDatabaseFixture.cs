using Dapper;
using Npgsql;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using DapperData = Pug.Scheduling.Data.Postgres.DapperData;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Provisions a fresh, isolated PostgreSQL database for the schedule store (migrated from the
/// <see cref="DapperData"/> schema scripts) in the run's shared container.
/// </summary>
public sealed class PostgresScheduleDatabaseFixture : IScheduleDatabaseFixture, IAsyncLifetime
{
	private string? _connectionString;

	public IApplicationData<IDataSession> ApplicationData { get; private set; } = null!;

	public async Task InitializeAsync()
	{
		_connectionString = await PostgresTestServer.CreateDatabaseAsync( "schedule" );

		DapperData data = new( _connectionString, NpgsqlFactory.Instance );

		PostgresTestServer.UpgradeDatabase( _connectionString, data.GetSchemaUpgradeScripts() );

		ApplicationData = data;
	}

	// now(), the autocommitted statement's start, rather than the store's clock_timestamp().
	public async Task<DateTime> SelectCurrentTimeAsync()
	{
		await using NpgsqlConnection connection = new( _connectionString );

		return await connection.ExecuteScalarAsync<DateTime>( "select now();" );
	}

	public async Task DisposeAsync()
	{
		if( _connectionString is not null )
			await PostgresTestServer.DropDatabaseAsync( _connectionString );
	}
}
