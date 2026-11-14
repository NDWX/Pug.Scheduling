# Pug.Scheduling

Generic, tenant-aware, DB-backed job scheduling framework (part of the `Pug.*` family), built for
scheduled report generation in the user's SaaS but application-agnostic. Design rationale and
phase status live in `docs/DECISIONS.md` — **read it before making design changes**; every major
shape here was explicitly decided with the user.

## Build & test gate (completion gate — never declare done without it)

```sh
dotnet build Pug.Scheduling.slnx -warnaserror -m:1
dotnet test tests/Pug.Scheduling.Tests
```

The Postgres test classes (`[Trait( "Category", "Postgres" )]`) start a Testcontainers PostgreSQL
and need Docker — on this machine run the tests as `sg docker -c "dotnet test tests/Pug.Scheduling.Tests"`.
Without Docker, `--filter "Category!=Postgres"` runs the rest.

## Solution layout

| Project | Role |
|---|---|
| `Pug.Scheduling.Models` | `WorkerInfo { Claimant, Worker, JobType, Paused, LastPoll, ActiveJobs }`, `ScheduleInfo { Identifier, Definition, Owner, State, ExecutionLease { Run, ClaimedBy, Expiry }, Registration/LastUpdate : ActionContext<Reference> }` (`State.Occurrence` = the retried occurrence while `Attempt > 0`), `ScheduleRun` (embeds `Outcome : JobOutcome`, keeps worker-classification `Result`), `ScheduledJobContext`, `ClaimedJob`, string-constant classes (`RunTriggers`, `RunResults`, `MisfireHandlings`). |
| `Pug.Scheduling.Abstractions` | `IScheduler` (config only), `IExecutionCoordinator` (workers' ONLY surface), `RunInProgressException` (run-now refused, 409), `ISchedulerWorker` (one worker) / `ISchedulerWorkerRegistry` (the host's workers by number), `IJobPayloadValidator`, `IJobTypeRegistry`, `IdentifierGenerator`, options records, security constants (in Abstractions by user decision). |
| `Pug.Scheduling.JobHandler.Abstractions` | `IScheduledJobHandler` (receives the timeout), `IScheduledJobHandlerFactory` (takes the context), `IScheduledJobHandlerFactoryRegistry` (null when none). Multi-target, depends on Models only. |
| `Pug.Scheduling` | `Scheduler` facade (validation, defaults, owner-rule-then-ACL authz), `ExecutionCoordinator` (claim/adopt/skip/renew/complete, one short method per transition and one `InUnitAsync` helper), `RecurrenceCalculator` (Cronos; `GetFirstOccurrence` for registration/recurrence change — an interval's `NotBefore` anchors its grid; `GetNextOccurrence( r, previous, after )` keeps the interval grid phase). **Every rule is a small pure function here**, unit-tested without a database: `OccurrenceState.Classify` (adopt > MANUAL > RETRY > MISFIRE/SKIP/SCHEDULE) and `ScheduledFor`, `CompletionRules.Next`/`Skipped`/`NextOccurrence` (retry, manual-run rules, the RUN_ALL baseline), `DefinitionRules.NextRunAfterUpdate`. Every state-dependent transition is **lock, decide, write** in one `UnitsOfWork.WriteAsync` unit: lock the schedule row (`GetScheduleAsync`/`GetScheduleByActiveRunAsync( …, lockRow: true )`), decide in C# on what was locked, write by identifier. A candidate that fails to claim is logged at Error and passed over. |
| `Pug.Scheduling.Data.Abstractions` | Schedule-store `IDataSession`: **storage primitives only** — reads (`GetScheduleAsync`/`GetScheduleByActiveRunAsync( …, lockRow )`, `GetDueCandidatesAsync`, runs) and unconditional writes by identifier (`SetExecutionAsync`, `SetOccurrenceStateAsync( OccurrenceProgress )`, `SetRunRequestedAsync`, `UpdateDefinitionAsync`, `SetPausedAsync`, `InsertRunAsync`, `FinishRunAsync`, `SetRunClaimantAsync`, `DeleteAsync`). No member decides anything; `GetDueCandidatesAsync` is a coarse pre-filter documented as mirroring `OccurrenceState.Classify`. Plus `UnitsOfWork` (`ReadAsync`/`WriteAsync`). |
| `Pug.Scheduling.Data.Dapper` | `DapperDataSessionBase`: the schedule store's SQL, `ScheduleRow`, `Compose` and `JsonTypeHandler`, written once for every provider. Its dialect hooks are `EnabledNotPausedPredicate`, `RunRequestedPredicate`, `LockingReadClause` and `ReadTimestamp`. The due-candidate read is a `union` of three bounded branches, one per partial index (an `or` is a full scan on SQLite). |
| `Pug.Scheduling.Data.Postgres` / `.SQLite` | Dapper schedule+run stores: **shared base + dialect overrides.** Each provider keeps a `DapperDataSession : DapperDataSessionBase` and its own `DapperData` (schema scripts). Postgres: `enabled and not paused`, `for update`, timestamps as stored. SQLite: `enabled = 1 and paused = 0`, no locking clause, D18 UTC stamping on read, and the session's own transaction enlistment (Conventions). SQL changes go in the base; a provider overrides only its dialect. |
| `Pug.Scheduling.Worker` | `SchedulerWorker : BackgroundService` — pure pump (poll → create handler → execute → dispose → heartbeat → report), one job at a time, constructed with its host-wide worker number and claiming as `( Claimant, number, JobType )`; pause/resume via `ISchedulerWorker`, fully defensive against third-party handlers. `WorkerOptions` (`Claimant` = machine name by default, `Workers`, `JobType`, intervals, `Validate()`); `SchedulerWorkerRegistry`. |
| `Pug.Scheduling.Worker.DependencyInjection` | net10.0 worker-host DI: `AddSchedulingWorkers( options, coordinator )` — once per worker group; numbers 1..N host-wide in registration order (accumulator singleton), one `IHostedService` singleton per worker (never `AddHostedService`: it dedups), optional per-group coordinator factory, registers `ISchedulerWorkerRegistry`; `AddScheduledJobHandler<T>(jobType)` (keyed scoped, one DI scope per run), `AddScheduledJobHandlerFactory<T>`; internal registry and scoped-handler wrapper. References no `Pug.Scheduling`/Data. Handler registration does not mark a type schedulable. |
| `Pug.Scheduling.WebApi.Models` | Every HTTP surface contract — `BadRequestResultInfo`, `JobRunClaimRequest`, `JobRunCompletion`. Referenced by `.WebApi` and `.Client` projects; carries no ASP.NET dependency. |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints()` (`schedules/…`) and `MapExecutionCoordinatorEndpoints()` (`executionCoordinator/…`, the out-of-process worker transport). Each extension carries its own route prefix — the host's routes are not the scheduler's alone. Multi-targeted, so it references only Abstractions/Models. |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints()` (`workers`, `workers/{worker}`, `workers/{worker}/pause|resume`; the list shows only workers whose claimant the caller may view). Its own package so an out-of-process worker host references nothing it does not need. |
| `Pug.Scheduling.Client` | `SchedulerClient` / `ExecutionCoordinatorClient` over the 1e routes; reproduces the facades' exceptions so callers cannot tell local from remote. References Abstractions only — a worker host must not pull in ASP.NET. |
| `Pug.Scheduling.DependencyInjection` | `AddScheduling`, `AddJobPayloadValidator<T>`, `AddSchedulableJobType`. |

## Domain invariants (do not regress)

- Workers never touch data stores — only `IExecutionCoordinator`.
- The execution lease lives on the schedule row (`activeRun`, `holder`, `leaseExpiry`, schema v3)
  and is written only inside the coordinator's transitions; `ScheduleInfo.ExecutionLease` is
  composed from those columns. A lapsed lease stays until its run is adopted or completed, so a
  schedule with a lease always has a run in progress. The holder on the row is the authority for
  renewal and completion; `ScheduleRun.ClaimedBy` is audit only.
- Every multi-statement unit is atomic on both providers, and every write that depends on the
  schedule's state locks the row first and decides on what it locked (no business logic in SQL).
- `Owner` is not part of `ScheduleDefinition`; assigned at registration (defaults to current
  principal), immutable. Owner rule: `Type == "USER" && Identifier == current` — covers creating
  own schedules (no ACL) and view/modify/delete of owned schedules. Domain is immutable.
- Idempotency key is `(Schedule, ScheduledFor)`: retries keep the original occurrence; orphaned
  unfinished runs are adopted on re-claim (adoption precedes every other rule, misfire SKIP
  included), never duplicated. A retry carries its occurrence on the row (`occurrence`).
- `RunRequested` marker ⇒ MANUAL trigger, consumed transactionally by the honoring claim. Run-now
  never moves `nextRun` and is refused (`RunInProgressException`, 409) while a run is in
  progress, live or lapsed. Manual runs are not retried; one during a retry's backoff is that retry.
  On completion FIRE_ONCE/SKIP coalesce an occurrence already due when it started; RUN_ALL keeps
  its backlog. A definition update moves `nextRun` only when idle and the recurrence changed.
- Handlers are third-party: escaping exceptions / null returns become unsuccessful `JobOutcome`s;
  timeouts are worker-enforced; claim loss or shutdown ⇒ abandon without reporting.
- All times UTC; cron in the recurrence's IANA time zone via Cronos.
- Endpoints authorize nothing themselves — the facades do. `Scheduler` runs owner-rule-then-ACL;
  `ExecutionCoordinator` checks `CoordinatorSecurityOperations` qualified by **claimant identity**
  (never in the endpoints: an in-process worker must be held to the same grants as a remote one).
  Endpoints only require authentication and map `NotAuthorized`/`Unknown*Exception`/
  `ArgumentException` to 403/404/400 (and `ClaimNotHeldException`/`RunInProgressException` to
  409). The lone exception is worker management, whose
  `ISchedulerWorker` has no facade behind it.
- The coordinator skips the check when `ISecurityManager` reports no principal: only in-process
  callers can reach it that way, since endpoints reject unauthenticated requests before dispatch.
  An in-process worker needs no service identity. `Scheduler` still requires a principal — it
  derives `Owner` and audit contexts from it.
- Every HTTP surface contract belongs in `Pug.Scheduling.WebApi.Models` — not in Models (they
  describe the transport, not the domain) and not in a `.WebApi` package (a worker host's client
  must not pull in ASP.NET).
- Worker numbers are host-wide and deterministic (registration order); the lease holder is
  `claimant/number`, so a restarted host with the same claimant adopts its own unfinished runs even
  while their leases are live. Lease expiry uses each coordinator host's clock: `LeaseDuration`
  must exceed the clock skew between hosts. Two worker processes on one machine must set distinct claimants (D20).
- All times are UTC and say so: the SQLite sessions stamp `DateTimeKind.Utc` on read, matching
  Postgres `timestamptz`. Do not regress this — the two providers must agree.

## Conventions

Tabs; block-scoped namespaces; spaces inside parens `Method( arg )`; `.ConfigureAwait( false )`
in library code; `[DataContract]` records with the `#if NET5_0_OR_GREATER init/set` accessor
pattern; data access via `UnitsOfWork`: `ApplicationDataProvider.ReadAsync/WriteAsync( ( session,
context ) => …, context: new { … } )`, which are `ExecuteAsync`/`PerformAsync` in a `RequiresNew` scope.
`ReadAsync` runs at RepeatableRead, for pure reads; `WriteAsync` runs at ReadCommitted, for every
unit that writes. A write unit that acts on what it read locks the row first (`lockRow`). Nothing in
the domain layer begins or commits a transaction. **SQLite enlists itself** (the Pug.Sal pattern):
Microsoft.Data.Sqlite ignores `TransactionScope`, so its `DapperData` enlists every session created in
a scope. A write unit begins `IMMEDIATE`, taking the write lock up front, and a read unit begins
deferred; both commit in phase one. Postgres must not copy this: Npgsql enlists natively.
Contracts multi-target `net6.0;net8.0;net10.0`,
implementations `net10.0`. Tests: xUnit, per-fixture databases migrated by DbUp from
`GetSchemaUpgradeScripts()`; Moq only for `ISecurityManager`. Store-backed suites are abstract
generics over `IScheduleDatabaseFixture` with a `Sqlite…` and a
`Postgres…` subclass each (Postgres: one shared Testcontainers server, a database per fixture);
the HTTP/API suites run on SQLite only. Test helpers touch the store through `UnitsOfWork`, and
lease lapse is simulated by advancing a `ManualTimeProvider` past `LeaseDuration`. DbUp sorts
scripts by name, so fixtures pass `RunGroupOrder` to keep the version/sequence order.

## Workflow requirement

Work proceeds in sub-phases with a **pause for user verification after every sub-phase**.
Current status and next steps: end of `docs/DECISIONS.md`.
