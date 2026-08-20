using System.Data;
using System.Data.Common;
using Pug.Application.Data;
using Pug.Scheduling.Leases.Data.Abstractions;
using static System.String;

namespace Pug.Scheduling.Leases.Data.SQLite
{
	// The lease store's own data layer: a single lease table, kept separate from the schedule
	// store (its own location / schema versioning — possibly a different database entirely).
	// DateTimes are stored as invariant ISO-8601 TEXT, which compares chronologically.
	public class DapperData : ApplicationData<IDataSession>
	{
		public DapperData( string location, DbProviderFactory dataProvider ) : base( location, dataProvider )
		{
		}

		protected override IDataSession CreateApplicationDataSession( IDbConnection databaseSession, DbProviderFactory dataAccessProvider )
		{
			return new DapperDataSession( databaseSession );
		}

		protected override IEnumerable<SchemaVersion> InitializeUpgradeScripts()
		{
			return new[]
			{
				new SchemaVersion(
						1, new[]
						{
							new UpgradeScript(
									"Execution lease table", Empty,
									@"create table executionLease(
											    schedule text not null primary key,
											    claimedBy text not null,
											    expiry text not null
											);
											create index ixExecutionLeaseExpiry on executionLease(expiry);",
									@"drop table executionLease;"
								)
						}
					)
			};
		}
	}
}
