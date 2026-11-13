# Known issues

## SQLite writes are not atomic (found 2026-10-09, from Pug.Sal phase 2)

**What happens.** Microsoft.Data.Sqlite ignores the ambient `System.Transactions` transaction. As
a result, the `TransactionScope` that `ApplicationData.ExecuteAsync` / `PerformAsync` (from
Pug.Application.Data) open around each unit of work has no effect on the SQLite schedule and lease
stores:

- **Every statement autocommits.** A unit of work that fails part-way leaves its earlier writes in
  place.
- **No write lock is held across the unit of work.** Concurrent read-then-write sequences can race.

The Postgres stores are not affected: Npgsql enlists in `TransactionScope` natively.

**How to reproduce.** Run a `PerformAsync` that inserts a row and then throws. The row is still
there afterwards.

**Not yet checked here.** Which Scheduling code paths actually depend on multi-statement
atomicity, or on a lock held across statements, on SQLite. The `IDataSession` comment says callers
"compose primitives into atomic units using the inherited transaction control".

**How Pug.Sal fixed it** (`Pug.Sal/src/Pug.Sal.Data.SQLite/DapperDataSession.cs`, `DapperData.cs`;
rationale in `Pug.Sal/docs/DECISIONS.md` D15):

- **The session enlists itself.** When a scope is active (`Transaction.Current`),
  `DapperData.CreateApplicationDataSession` opens a real SQLite transaction on the session
  (`BeginTransaction()`). It then enlists an `IEnlistmentNotification` through `EnlistVolatile`,
  which ties that transaction to the scope's outcome.
- **It commits in phase one (`Prepare`), not in `Commit`.** A commit that fails (e.g.
  `SQLITE_BUSY`) then calls `ForceRollback`, so the scope's `Dispose` throws. A commit in phase two
  would fail only after the scope had already reported success.
- **Closing the connection waits until the scope ends.** ApplicationData disposes the session
  before the scope completes, so the session's `Dispose` defers until the enlistment has finished.
- **Limits:**
  - A session created inside an already-open ambient transaction (a nested `Required` scope) gets
    its own connection and transaction.
  - Committing in phase one cannot be undone if another participant in the same scope later fails.
- **Locking:** Sal also takes the write lock up front, with a no-op `update` before its first read.
  Under a deferred `BEGIN` that is what serializes concurrent read-then-write units of work.
- **Tests:** Sal checks the fix with a rollback test (`FailedUnitOfWorkRollsBackEveryWrite`) and a
  concurrency test (`ConcurrentTransactionsGetDistinctVersions`, six parallel units of work).
