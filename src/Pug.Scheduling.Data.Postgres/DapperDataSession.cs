using System.Data;
using Pug.Scheduling.Data.Abstractions;
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

		protected override string RunRequestedPredicate => "runRequested";

		// Holds the row's lock until the unit's transaction ends, so a read the unit then acts on
		// cannot be overtaken by a concurrent writer under READ COMMITTED. 'for update' waits for a
		// concurrent holder and then reads the row as it committed; 'skip locked' returns no row
		// instead of waiting, so a held row reads like a missing one.
		protected override string LockingReadClause( RowLock rowLock ) =>
			rowLock switch
			{
				RowLock.None => string.Empty,
				RowLock.Wait => "for update",
				RowLock.SkipLocked => "for update skip locked",
				_ => throw new ArgumentOutOfRangeException( nameof(rowLock), rowLock, null )
			};

		// clock_timestamp(), not now(): now() is the transaction's start, which a unit that waited
		// on a row lock has long passed. Microsecond precision, timestamptz, so it comes back UTC.
		protected override string CurrentTimeExpression => "clock_timestamp()";
	}
}
