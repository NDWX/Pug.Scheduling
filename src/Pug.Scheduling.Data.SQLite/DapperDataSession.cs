using System.Data;
using System.Transactions;
using Microsoft.Data.Sqlite;
using Pug.Scheduling.Data.Dapper;

namespace Pug.Scheduling.Data.SQLite
{
	// The SQLite dialect of the schedule store, plus the transaction enlistment Microsoft.Data.Sqlite
	// lacks (ported from Pug.Sal, its DECISIONS D15/D19).
	public class DapperDataSession : DapperDataSessionBase
	{
		public DapperDataSession( IDbConnection databaseSession ) : base( databaseSession )
		{
		}

		// == Dialect ==

		// Booleans are stored as integers.
		protected override string EnabledNotPausedPredicate => "enabled = 1 and paused = 0";

		// A write unit is BEGIN IMMEDIATE, which already holds the database write lock; SQLite has
		// no row locks (and no 'for update').
		protected override string LockingReadClause => string.Empty;

		// SQLite stores timestamps as ISO-8601 text without an offset, so the provider hands them
		// back as DateTimeKind.Unspecified. Every time in this domain is UTC (and the Postgres
		// provider's timestamptz columns say so), which makes stamping the kind on read the
		// difference between the two providers agreeing and a consumer's ToLocalTime() silently
		// shifting the value (DECISIONS D18).
		protected override DateTime ReadTimestamp( DateTime stored ) => DateTime.SpecifyKind( stored, DateTimeKind.Utc );

		// == Ambient transaction enlistment ==

		// Microsoft.Data.Sqlite ignores an ambient System.Transactions transaction: statements would
		// autocommit, giving ApplicationData's TransactionScope-based ExecuteAsync/PerformAsync no
		// atomicity and no write lock held across a unit of work. So the session opens a real SQLite
		// transaction when created inside a scope and ties its commit/rollback to the scope's outcome.
		//
		// Which kind of SQLite transaction depends on the unit of work (UnitsOfWork):
		// - A write (ReadCommitted, or any level other than below) is IMMEDIATE: it takes the write
		//   lock at BEGIN, so writers queue up front instead of failing to upgrade a read lock, and a
		//   read the unit then acts on cannot be invalidated by another writer.
		// - A read (RepeatableRead or Snapshot) is DEFERRED: it takes no lock until its first read,
		//   then sees one snapshot (in WAL mode) without blocking writers or other readers.
		// Microsoft.Data.Sqlite begins IMMEDIATE for every level but ReadUncommitted, so the deferred
		// transaction needs SqliteConnection's own overload, outside ApplicationDataSession's
		// transaction chain.
		private ScopeEnlistment? _enlistment;

		private SqliteTransaction? _deferred;

		internal void EnlistInAmbientTransaction( Transaction transaction )
		{
			if( IsRead( transaction.IsolationLevel ) && Connection is SqliteConnection connection )
				_deferred = connection.BeginTransaction( deferred: true );
			else
				BeginTransaction();

			_enlistment = new ScopeEnlistment( this );
			transaction.EnlistVolatile( _enlistment, EnlistmentOptions.None );
		}

		private static bool IsRead( System.Transactions.IsolationLevel level ) =>
			level is System.Transactions.IsolationLevel.RepeatableRead or System.Transactions.IsolationLevel.Snapshot;

		private void CommitEnlisted()
		{
			if( _deferred is null )
			{
				CommitTransaction();

				return;
			}

			_deferred.Commit();
			_deferred.Dispose();
			_deferred = null;
		}

		private void RollbackEnlisted()
		{
			if( _deferred is null )
			{
				RollbackTransaction();

				return;
			}

			try
			{
				_deferred.Rollback();
			}
			finally
			{
				_deferred.Dispose();
				_deferred = null;
			}
		}

		// ApplicationData disposes the session before the scope completes, so while an enlisted
		// transaction is still pending, closing the connection is left to the enlistment.
		public override void Dispose()
		{
			if( _enlistment is not null && !_enlistment.Completed )
			{
				_enlistment.DisposeRequested = true;
				return;
			}

			base.Dispose();
		}

		private sealed class ScopeEnlistment : IEnlistmentNotification
		{
			private readonly DapperDataSession _session;

			public ScopeEnlistment( DapperDataSession session )
			{
				_session = session;
			}

			public bool Completed { get; private set; }

			public bool DisposeRequested { get; set; }

			private bool _committed;

			// The SQLite transaction is committed here, in phase one, not in Commit: a commit that
			// fails (e.g. SQLITE_BUSY) must abort the scope so its Dispose throws, rather than surface
			// after the scope has already reported success. Committing in phase one cannot be undone
			// if another participant of the same scope then fails; the schedule store's units of work
			// (UnitsOfWork, RequiresNew) enlist nothing else.
			public void Prepare( PreparingEnlistment preparingEnlistment )
			{
				try
				{
					_session.CommitEnlisted();
				}
				catch( Exception exception )
				{
					try
					{
						_session.RollbackEnlisted();
					}
					catch
					{
						// The commit failure is the error worth reporting.
					}

					Release();
					preparingEnlistment.ForceRollback( exception );

					return;
				}

				_committed = true;
				preparingEnlistment.Prepared();
			}

			public void Commit( Enlistment enlistment ) => Finish( enlistment );

			public void Rollback( Enlistment enlistment ) => Abandon( enlistment );

			public void InDoubt( Enlistment enlistment ) => Abandon( enlistment );

			private void Abandon( Enlistment enlistment )
			{
				try
				{
					if( !_committed )
						_session.RollbackEnlisted();
				}
				finally
				{
					Finish( enlistment );
				}
			}

			private void Finish( Enlistment enlistment )
			{
				try
				{
					Release();
				}
				finally
				{
					enlistment.Done();
				}
			}

			private void Release()
			{
				if( Completed )
					return;

				Completed = true;

				if( DisposeRequested )
					_session.Dispose();
			}
		}
	}
}
