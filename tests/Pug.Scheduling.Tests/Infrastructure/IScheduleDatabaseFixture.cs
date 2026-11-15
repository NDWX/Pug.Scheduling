using Pug.Application.Data;
using Pug.Scheduling.Data.Abstractions;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// A provisioned, migrated schedule store database. The store-backed suites are written against
/// this, so one suite runs unchanged over every provider's fixture.
/// </summary>
public interface IScheduleDatabaseFixture
{
	IApplicationData<IDataSession> ApplicationData { get; }

	/// <summary>
	/// The database's current UTC time, selected on a connection of its own and written differently
	/// from the store's own clock read, so the two check each other.
	/// </summary>
	Task<DateTime> SelectCurrentTimeAsync();

	/// <summary>
	/// The database's current UTC time as the store's own clock reads it: a
	/// <see cref="DatabaseClock"/> in a read unit of work.
	/// </summary>
	Task<DateTime> ReadDatabaseClockAsync() =>
		ApplicationData.ReadAsync( ( session, clock ) => clock.GetUtcNowAsync( session ), new DatabaseClock() );
}
