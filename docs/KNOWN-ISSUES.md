# Known issues

## SQLite writes are not atomic (found 2026-10-09, from Pug.Sal phase 2)

**RESOLVED for Pug.Scheduling in #10, sub-phase A** (2026-11-14), with the Pug.Sal pattern described
below: the SQLite session now enlists itself, so units of work are atomic and write units hold the
write lock. See `DECISIONS.md` D24. The enlistment code now exists in both repos; moving it to a
shared package is tracked in #13. The original report follows unchanged.

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

## Schedules stalled under schema v2 stay stuck after the v3 migration (noted 2026-11-14, #10)

**What happens.** Under v2, completion could write back a stale `nextRun` (#8, F3), leaving a
schedule whose `nextRun` points at an occurrence that already finished. Every claim then finds that
occurrence done and discards the candidate, so the schedule stalls. The v3 migration backfills the
execution columns but does not repair `nextRun`, so such a schedule stays stalled.

**Workaround.** Run-now unsticks a FIRE_ONCE or SKIP schedule: the manual run completes and
coalesces the overdue occurrence onto the grid. It does not unstick a RUN_ALL schedule, which keeps
its backlog by design; update its recurrence while idle, or re-register it.

**Status.** Accepted: the package is pre-release and no v2 database is deployed.
