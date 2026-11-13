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
}
