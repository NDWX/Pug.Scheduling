using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using Npgsql;
using Pug.Application.Data;
using Testcontainers.PostgreSql;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// The one PostgreSQL container of a test run, started on first use and shared by every Postgres
/// fixture; each fixture gets a database of its own in it. Testcontainers' resource reaper
/// removes the container when the test process exits.
/// </summary>
internal static class PostgresTestServer
{
	private static readonly Lazy<Task<PostgreSqlContainer>> Container =
		new( StartAsync, LazyThreadSafetyMode.ExecutionAndPublication );

	private static async Task<PostgreSqlContainer> StartAsync()
	{
		PostgreSqlContainer container =
			new PostgreSqlBuilder( "postgres:17-alpine" )
				// Fixtures of concurrently running classes each keep their own connection pools.
				// Durability is irrelevant to a throwaway database.
				.WithCommand( "-c", "max_connections=500", "-c", "fsync=off", "-c", "synchronous_commit=off" )
				.Build();

		await container.StartAsync().ConfigureAwait( false );

		return container;
	}

	/// <summary>Creates an empty, uniquely named database and returns its connection string.</summary>
	public static async Task<string> CreateDatabaseAsync( string prefix )
	{
		PostgreSqlContainer container = await Container.Value.ConfigureAwait( false );

		string database = $"{prefix}_{Guid.NewGuid():N}";

		await using( NpgsqlConnection connection = new( container.GetConnectionString() ) )
		{
			await connection.OpenAsync().ConfigureAwait( false );

			await using NpgsqlCommand command = new( $"create database \"{database}\";", connection );

			await command.ExecuteNonQueryAsync().ConfigureAwait( false );
		}

		return new NpgsqlConnectionStringBuilder( container.GetConnectionString() ) { Database = database }
				.ConnectionString;
	}

	public static void UpgradeDatabase( string connectionString, IEnumerable<SchemaUpgradeScript> schemaScripts )
	{
		UpgradeEngineBuilder builder = DeployChanges.To.PostgresqlDatabase( connectionString );

		foreach( SchemaUpgradeScript schemaScript in schemaScripts )
			builder = builder.WithScript( schemaScript.UpgradeScript.Name, schemaScript.UpgradeScript.Script );

		DatabaseUpgradeResult result = builder.LogToNowhere().Build().PerformUpgrade();

		if( !result.Successful )
			throw new InvalidOperationException( "DbUp schema migration failed.", result.Error );
	}

	public static async Task DropDatabaseAsync( string connectionString )
	{
		PostgreSqlContainer container = await Container.Value.ConfigureAwait( false );

		NpgsqlConnectionStringBuilder builder = new( connectionString );

		await using( NpgsqlConnection pooled = new( connectionString ) )
			NpgsqlConnection.ClearPool( pooled );

		await using NpgsqlConnection connection = new( container.GetConnectionString() );

		await connection.OpenAsync().ConfigureAwait( false );

		await using NpgsqlCommand command =
			new( $"drop database if exists \"{builder.Database}\" with (force);", connection );

		await command.ExecuteNonQueryAsync().ConfigureAwait( false );
	}
}
