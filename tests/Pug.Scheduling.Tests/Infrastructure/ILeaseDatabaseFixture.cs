using Pug.Application.Data;
using Pug.Scheduling.Leases.Data.Abstractions;

namespace Pug.Scheduling.Tests.Infrastructure;

/// <summary>
/// A provisioned, migrated execution-lease store database. The store-backed suites are written
/// against this, so one suite runs unchanged over every provider's fixture.
/// </summary>
public interface ILeaseDatabaseFixture
{
	IApplicationData<IDataSession> ApplicationData { get; }
}
