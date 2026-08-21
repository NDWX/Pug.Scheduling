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

## Solution layout

| Project | Role |
|---|---|
| `Pug.Scheduling.Models` | `WorkerInfo`, `ScheduleInfo { Identifier, Definition, Owner, State, ExecutionLease, Registration/LastUpdate : ActionContext<Reference> }`, `ScheduleRun` (embeds `Outcome : JobOutcome`, keeps worker-classification `Result`), `ScheduledJobContext`, `ClaimedJob`, string-constant classes (`RunTriggers`, `RunResults`, `MisfireHandlings`). |
| `Pug.Scheduling.Abstractions` | `IScheduler` (config only), `IExecutionCoordinator` (workers' ONLY surface), `IExecutionLeaseProvider`, `IScheduledJobHandler`/`IScheduledJobDispatcher`, `IJobPayloadValidator`, `IJobTypeRegistry`, `IdentifierGenerator`, options records, security constants (in Abstractions by user decision). |
| `Pug.Scheduling` | `Scheduler` facade (validation, defaults, owner-rule-then-ACL authz), `ExecutionCoordinator` (claim/misfire/retry/completion state machine), `RecurrenceCalculator` (Cronos). |
| `Pug.Scheduling.Data.Abstractions` | Schedule-store `IDataSession` (no lease semantics; plain `GetDueSchedulesAsync`). |
| `Pug.Scheduling.Data.Postgres` / `.SQLite` | Dapper schedule+run stores. **Postgres session is generated from the SQLite session** (`sed`: namespace + `enabled = 1 and paused = 0` → `enabled and not paused`) — keep them in sync. |
| `Pug.Scheduling.Leases[.Data.*]` | `DatabaseExecutionLeaseProvider` over its OWN store (conditional-upsert arbitration). Lease state never touches the schedule table. |
| `Pug.Scheduling.Worker` | `SchedulerWorker : BackgroundService` — pure pump (poll → dispatch → heartbeat → report), pause/resume via `ISchedulerWorker`, fully defensive against third-party handlers. |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints()` (`schedules/…`) and `MapExecutionCoordinatorEndpoints()` (`executionCoordinator/…`, the out-of-process worker transport). Each extension carries its own route prefix — the host's routes are not the scheduler's alone. Multi-targeted, so it references only Abstractions/Models. |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints()` (`worker/…`). Its own package so an out-of-process worker host references nothing it does not need. |
| `Pug.Scheduling.DependencyInjection` | `AddScheduling`, `AddSchedulingWorker`, `AddScheduledJobHandler<T>(jobType)` (keyed scoped), `AddJobPayloadValidator<T>`, `AddSchedulableJobType`; `InProcessJobDispatcher` (DI scope per run). |

## Domain invariants (do not regress)

- Workers never touch data stores — only `IExecutionCoordinator`.
- Lease state lives only behind `IExecutionLeaseProvider`; `ScheduleInfo.ExecutionLease` is
  composed at read time.
- `Owner` is not part of `ScheduleDefinition`; assigned at registration (defaults to current
  principal), immutable. Owner rule: `Type == "USER" && Identifier == current` — covers creating
  own schedules (no ACL) and view/modify/delete of owned schedules. Domain is immutable.
- Idempotency key is `(Schedule, ScheduledFor)`: retries keep the original occurrence; orphaned
  unfinished runs are adopted on re-claim, never duplicated.
- `RunRequested` marker ⇒ MANUAL trigger, consumed transactionally by the honoring claim.
- Handlers are third-party: escaping exceptions / null returns become unsuccessful `JobOutcome`s;
  timeouts are worker-enforced; claim loss or shutdown ⇒ abandon without reporting.
- All times UTC; cron in the recurrence's IANA time zone via Cronos.
- Endpoints authorize nothing themselves — the facades do. `Scheduler` runs owner-rule-then-ACL;
  `ExecutionCoordinator` checks `CoordinatorSecurityOperations` qualified by **claimant identity**
  (never in the endpoints: an in-process worker must be held to the same grants as a remote one).
  Endpoints only require authentication and map `NotAuthorized`/`Unknown*Exception`/
  `ArgumentException` to 403/404/400. The lone exception is worker management, whose
  `ISchedulerWorker` has no facade behind it.
- The coordinator skips the check when `ISecurityManager` reports no principal: only in-process
  callers can reach it that way, since endpoints reject unauthenticated requests before dispatch.
  An in-process worker needs no service identity. `Scheduler` still requires a principal — it
  derives `Owner` and audit contexts from it.
- Coordinator wire contracts (`JobRunClaimRequest`, `JobRunCompletion`) live in Models, never in
  the WebApi: a worker host's client must not pull in the server-side package.

## Conventions

Tabs; block-scoped namespaces; spaces inside parens `Method( arg )`; `.ConfigureAwait( false )`
in library code; `[DataContract]` records with the `#if NET5_0_OR_GREATER init/set` accessor
pattern; data access via `ApplicationDataProvider.ExecuteAsync/PerformAsync( async ( session,
context ) => …, context: new { … } )`; contracts multi-target `net6.0;net8.0;net10.0`,
implementations `net10.0`. Tests: xUnit, per-fixture SQLite temp DBs migrated by DbUp from
`GetSchemaUpgradeScripts()`; Moq only for `ISecurityManager`. No Docker here ⇒ Postgres providers
are build-verified only (CI should run Testcontainers races).

## Workflow requirement

Work proceeds in sub-phases with a **pause for user verification after every sub-phase**.
Current status and next steps: end of `docs/DECISIONS.md`.
