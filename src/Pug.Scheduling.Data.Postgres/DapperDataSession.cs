using System.Data;
using Pug.Scheduling.Data.Dapper;

namespace Pug.Scheduling.Data.Postgres
{
	// The PostgreSQL dialect of the schedule store. Npgsql enlists in the ambient TransactionScope
	// natively, so unlike SQLite this session needs no enlistment of its own; timestamptz columns
	// come back as UTC, so timestamps are read as stored.
	public class DapperDataSession : DapperDataSessionBase
	{
		public DapperDataSession( IDbConnection databaseSession ) : base( databaseSession )
		{
		}

		protected override string EnabledNotPausedPredicate => "enabled and not paused";

		// Holds the row's lock until the unit's transaction ends, so a read the unit then acts on
		// cannot be overtaken by a concurrent writer under READ COMMITTED.
		protected override string LockingReadClause => "for update";
	}
}
