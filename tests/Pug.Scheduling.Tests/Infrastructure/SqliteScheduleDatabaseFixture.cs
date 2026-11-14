using DbUp;
using Microsoft.Data.Sqlite;
using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;
using DapperData = Pug.Scheduling.Data.SQLite.DapperData;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// Provisions a fresh, isolated SQLite database for the schedule store (migrated from the
/// <see cref="DapperData"/> schema scripts).
/// </summary>
public sealed class SqliteScheduleDatabaseFixture : IScheduleDatabaseFixture, IAsyncLifetime
{
	private string? _dbPath;
	private bool _disposed;

	public IApplicationData<IDataSession> ApplicationData { get; private set; } = null!;

	public Task InitializeAsync()
	{
		_dbPath = Path.Combine( Path.GetTempPath(), $"pug_scheduling_schedule_test_{Guid.NewGuid():N}.db" );

		// Pooling=False ensures each connection is physically closed when disposed.
		string connectionString = $"Data Source={_dbPath};Pooling=False;";

		DapperData data = new( connectionString, SqliteFactory.Instance );

		UpgradeDatabase( connectionString, data );

		ApplicationData = data;

		return Task.CompletedTask;
	}

	public Task DisposeAsync()
	{
		Cleanup();
		return Task.CompletedTask;
	}

	private static void UpgradeDatabase( string connectionString, DapperData data )
	{
		SchemaUpgrade.Apply( DeployChanges.To.SqliteDatabase( connectionString ), data.GetSchemaUpgradeScripts() );

		using SqliteConnection connection = new( connectionString );
		connection.Open();
		using SqliteCommand cmd = connection.CreateCommand();
		cmd.CommandText = "PRAGMA journal_mode=WAL;";
		cmd.ExecuteNonQuery();
		cmd.CommandText = "PRAGMA busy_timeout=5000;";
		cmd.ExecuteNonQuery();
	}

	private void Cleanup()
	{
		if( _disposed ) return;
		_disposed = true;

		if( _dbPath != null && File.Exists( _dbPath ) )
			File.Delete( _dbPath );
	}
}
