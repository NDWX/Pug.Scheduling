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
| `Pug.Scheduling.Models` | `WorkerInfo { Claimant, Worker, JobType, Paused, LastPoll, ActiveJobs, DetachedJobs }`, `ScheduleInfo { Identifier, Definition, Owner, State, ExecutionLease { Run, ClaimedBy, Expiry }, Registration/LastUpdate : ActionContext<Reference> }` (`State.Occurrence` = the retried occurrence while `Attempt > 0`), `ScheduleRun` (embeds `Outcome : JobOutcome`, keeps worker-classification `Result`), `ScheduledJobContext`, `ClaimedJob`, string-constant classes (`RunTriggers`, `RunResults`, `MisfireHandlings`). |
| `Pug.Scheduling.Abstractions` | `IScheduler` (config only), `IExecutionCoordinator` (workers' ONLY surface), `RunInProgressException` (run-now refused, 409), `ISchedulerWorker` (one worker) / `ISchedulerWorkerRegistry` (the host's workers by number), `IJobPayloadValidator`, `IJobTypeRegistry`, `IdentifierGenerator`, options records, security constants (in Abstractions by user decision). |
| `Pug.Scheduling.JobHandler.Abstractions` | `IScheduledJobHandler` (receives the timeout), `IScheduledJobHandlerFactory` (takes the context), `IScheduledJobHandlerFactoryRegistry` (null when none). Multi-target, depends on Models only. |
| `Pug.Scheduling` | `Scheduler` facade (validation — durations are whole seconds, `SchedulerOptions.MinimumMisfireThreshold` floors misfire thresholds, a specified timeout and `DefaultTimeout` are at least 5 s (`DurationRules.MinimumTimeout`) — defaults, owner-rule-then-ACL authz), `ExecutionCoordinator` (claim/adopt/skip/renew/complete, one short method per transition and one `InUnitAsync` helper), `RecurrenceCalculator` (Cronos; `GetFirstOccurrence` for registration/recurrence change — an interval's `NotBefore` anchors its grid; `GetNextOccurrence( r, previous, after )` keeps the interval grid phase). **Every rule is a small pure function here**, unit-tested without a database: `OccurrenceState.Classify` (adopt > MANUAL > RETRY > MISFIRE/SKIP/SCHEDULE) and `ScheduledFor`, `CompletionRules.Next`/`Skipped`/`NextOccurrence` (retry, manual-run rules, the RUN_ALL baseline), `DefinitionRules.NextRunAfterUpdate`. Every state-dependent transition is **lock, decide, write** in one `UnitsOfWork.WriteAsync` unit: lock the schedule row (`GetScheduleAsync`/`GetScheduleByActiveRunAsync( …, RowLock.Wait )`; the claim alone uses `RowLock.SkipLocked`, so it passes over a candidate a peer holds), decide in C# on what was locked, write by identifier. A candidate that fails to claim is logged at Error and passed over. **Time comes from `ISchedulingClock`**, read through the unit's session inside the unit and after its lock (`GetUtcNowAsync( session )`): `DatabaseClock` (default) or `TimeProviderClock( TimeProvider )` (tests, single host). The facades hold no `TimeProvider`. |
| `Pug.Scheduling.Data.Abstractions` | Schedule-store `IDataSession`: **storage primitives only** — the database's current time (`GetCurrentTimeAsync`), reads (`GetScheduleAsync`/`GetScheduleByActiveRunAsync( …, RowLock )`, `GetDueCandidatesAsync`, runs) and unconditional writes by identifier (`SetExecutionAsync`, `SetOccurrenceStateAsync( OccurrenceProgress )`, `SetRunRequestedAsync`, `UpdateDefinitionAsync`, `SetPausedAsync`, `InsertRunAsync`, `FinishRunAsync`, `SetRunClaimantAsync`, `DeleteAsync`). No member decides anything; `GetDueCandidatesAsync` is a coarse pre-filter documented as mirroring `OccurrenceState.Classify`. Plus `UnitsOfWork` (`ReadAsync`/`WriteAsync`). |
| `Pug.Scheduling.Data.Dapper` | `DapperDataSessionBase`: the schedule store's SQL, `ScheduleRow`, `Compose` and `JsonTypeHandler`, written once for every provider. Its dialect hooks are `EnabledNotPausedPredicate`, `RunRequestedPredicate`, `LockingReadClause( RowLock )`, `CurrentTimeExpression` and `ReadTimestamp`. The due-candidate read is a `union` of three bounded branches, one per partial index (an `or` is a full scan on SQLite). |
| `Pug.Scheduling.Data.Postgres` / `.SQLite` | Dapper schedule+run stores: **shared base + dialect overrides.** Each provider keeps a `DapperDataSession : DapperDataSessionBase` and its own `DapperData` (schema scripts). Postgres: `enabled and not paused`, `for update` / `for update skip locked`, `clock_timestamp()` (never `now()`: the transaction's start), timestamps as stored. SQLite: `enabled = 1 and paused = 0`, no locking clause in any `RowLock` mode, `strftime( …%f, 'now' )` (the host's clock, milliseconds), D18 UTC stamping on read, and the session's own transaction enlistment (Conventions). SQL changes go in the base; a provider overrides only its dialect. |
| `Pug.Scheduling.Worker` | `SchedulerWorker : BackgroundService` — pump (poll → create handler → execute off the pump → heartbeat → dispose → report), **one attended job at a time**: a handler that ignores cancellation is cancelled, given `CancellationGrace`, then **detached** (reported TIMED_OUT, or abandoned without a report on claim loss/shutdown) and disposed when its task ends; at `MaxDetachedHandlers` the worker stops claiming. On shutdown, with the handler stopped (never detached, never claim lost), it releases its claim via `AbandonClaimAsync` (best effort, bounded by `AbandonTimeout`; D27). Constructed with its host-wide worker number and claiming as `( Claimant, number, JobType )`; pause/resume via `ISchedulerWorker`, fully defensive against third-party handlers. `WorkerOptions` (`Claimant` = machine name by default, `Workers`, `JobType`, `PollInterval`, `HeartbeatInterval`, `CancellationGrace` = 10 s, `MaxDetachedHandlers` = 2, `AbandonTimeout` = 5 s, `Validate()` incl. `TimerBounds`); `SchedulerWorkerRegistry`. |
| `Pug.Scheduling.Worker.DependencyInjection` | net10.0 worker-host DI: `AddSchedulingWorkers( options, coordinator )` — once per worker group; numbers 1..N host-wide in registration order (accumulator singleton), one `IHostedService` singleton per worker (never `AddHostedService`: it dedups), optional per-group coordinator factory, registers `ISchedulerWorkerRegistry`; `AddScheduledJobHandler<T>(jobType)` (keyed scoped, one DI scope per run), `AddScheduledJobHandlerFactory<T>`; internal registry and scoped-handler wrapper. References no `Pug.Scheduling`/Data. Handler registration does not mark a type schedulable. |
| `Pug.Scheduling.WebApi.Models` | Every HTTP surface contract — `BadRequestResultInfo`, `RunInProgressResultInfo` (the 409 body of a refused run-now), `JobRunClaimRequest`, `JobRunCompletion`, `ApiBasePaths` (default base paths and their normalization, shared by servers and clients). Referenced by `.WebApi` and `.Client` projects; carries no ASP.NET dependency. |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints()` (`schedules/…`) and `MapExecutionCoordinatorEndpoints()` (`executionCoordinator/…` — claims, `leaseRenewal`, `abandonment`, `completion` — the out-of-process worker transport; operations `CLAIM_JOBS`, `RENEW_CLAIM`, `ABANDON_CLAIM`, `REPORT_COMPLETION`). Each extension carries its own route prefix — the host's routes are not the scheduler's alone. Each takes optional `SchedulingEndpointOptions` / `ExecutionCoordinatorEndpointOptions` (`BasePath`, `AuthorizationPolicies`; D28), maps under a `RouteGroupBuilder` it returns, and applies `ApiAccess.RequireAuthenticatedPrincipal` once on the group (the host's policies or `DefaultPolicy`, plus the authenticated-caller-with-principal filter, which needs `ISecurityManager` in DI). Targets `net8.0;net10.0` (`MapGroup`); references only Abstractions/Models. |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints()` (`workers`, `workers/{worker}`, `workers/{worker}/pause|resume`; the list shows only workers whose claimant the caller may view); takes `SchedulerWorkerEndpointOptions` likewise; compiles WebApi's `ApiAccess.cs` as a linked source file rather than referencing that package. Targets `net8.0;net10.0`. Its own package so an out-of-process worker host references nothing it does not need. |
| `Pug.Scheduling.Client` | `SchedulerClient` / `ExecutionCoordinatorClient` over the 1e routes (optional `basePath`, default from `ApiBasePaths`; D28); reproduces the facades' exceptions so callers cannot tell local from remote. References Abstractions, WebApi.Models, `Pug.HttpApiClient` and `Pug.Application.Security.Abstractions` — a worker host must not pull in ASP.NET. |
| `Pug.Scheduling.DependencyInjection` | `AddScheduling` (also `TryAddSingleton<ISchedulingClock, DatabaseClock>`: a host's own `ISchedulingClock` wins, a registered `TimeProvider` does not), `AddJobPayloadValidator<T>`, `AddSchedulableJobType`. |

## Domain invariants (do not regress)

- Workers never touch data stores — only `IExecutionCoordinator`.
- The execution lease lives on the schedule row (`activeRun`, `holder`, `leaseExpiry`, schema v3)
  and is written only inside the coordinator's transitions; `ScheduleInfo.ExecutionLease` is
  composed from those columns. A lapsed lease stays until its run is adopted or completed, so a
  schedule with a lease always has a run in progress. The holder on the row is the authority for
  renewal and completion; `ScheduleRun.ClaimedBy` is audit only.
- Every multi-statement unit is atomic on both providers (SQLite through its session's own
  enlistment), and every write that depends on the schedule's state locks the row first and decides
  on what it locked.
- No business logic in the data access layer: sessions read, lock and write by identifier. The due
  query is the one SQL that mirrors a rule, as a documented pre-filter of `OccurrenceState.Classify`.
- `Owner` is not part of `ScheduleDefinition`; assigned at registration (defaults to current
  principal), immutable. Owner rule: `Type == "USER" && Identifier == current` — covers creating
  own schedules (no ACL) and view/modify/delete of owned schedules. Registering on behalf of
  another owner checks `CREATE_ON_BEHALF` against the owner principal (`Type`/`Identifier` =
  owner's, `Domain` = definition's); there is no `CREATE` operation. Domain is immutable.
- Idempotency key is `(Schedule, ScheduledFor)`: retries keep the original occurrence; orphaned
  unfinished runs are adopted on re-claim (adoption precedes every other rule, misfire SKIP
  included), never duplicated. A retry carries its occurrence on the row (`occurrence`).
- `RunRequested` marker ⇒ MANUAL trigger, consumed transactionally by the honoring claim. Run-now
  never moves `nextRun` and is refused (`RunInProgressException`, 409) while a run is in
  progress, live or lapsed. Manual runs are not retried; one during a retry's backoff is that retry.
  On completion FIRE_ONCE/SKIP coalesce an occurrence already due when it started; RUN_ALL keeps
  its backlog. A definition update moves `nextRun` only when idle and the recurrence changed.
- Handlers are third-party: escaping exceptions / null returns become unsuccessful `JobOutcome`s;
  timeouts are worker-enforced; claim loss or shutdown ⇒ abandon without reporting (a shutdown with the handler stopped also releases the claim, D27). A worker
  attends a single job at once; a handler that ignores cancellation is detached (it may overlap its own
  retry) and counted in `DetachedJobs`.
- All times UTC; cron in the recurrence's IANA time zone via Cronos.
- Endpoints make no grant decisions — the facades do. The group applies the host's policies for that
  API, or its `DefaultPolicy` when none are given (a transport gate: who may call it at all, D28), and
  always requires an authenticated caller with a principal. `Scheduler` runs owner-rule-then-ACL;
  `ExecutionCoordinator` checks `CoordinatorSecurityOperations` qualified by **claimant identity**
  (never in the endpoints: an in-process worker must be held to the same grants as a remote one).
  Endpoints only require an authenticated caller with a principal (always, plus those policies)
  and map `NotAuthorized`/`Unknown*Exception`/
  `ArgumentException` to 403/404/400 (and `ClaimNotHeldException`/`RunInProgressException` to
  409). The lone exception is worker management, whose
  `ISchedulerWorker` has no facade behind it.
- The coordinator skips the check when `ISecurityManager` reports no principal. Under no
  supported configuration does a call through the `Map…Endpoints` reach it that way: every API
  group carries an endpoint filter (`ApiAccess`) that refuses an unauthenticated caller (401) or
  one `ISecurityManager` maps to no principal (403), whatever the policies admit and even under
  `.AllowAnonymous()`. Policies: no names → the host's `DefaultPolicy`; names → an inline
  authenticated-user policy **plus** the named ones. The filter makes no grant decision; do not
  regress it. Any other transport in front of the coordinator (a host's controller, gRPC, a
  message consumer) must establish a principal itself, or every coordinator grant is skipped. An in-process worker needs no service identity. `Scheduler` still requires a principal — it
  derives `Owner` and audit contexts from it.
- Every HTTP surface contract belongs in `Pug.Scheduling.WebApi.Models` — not in Models (they
  describe the transport, not the domain) and not in a `.WebApi` package (a worker host's client
  must not pull in ASP.NET).
- Worker numbers are host-wide and deterministic (registration order); the lease holder is
  `claimant/number`, so a restarted host with the same claimant adopts its own unfinished runs even
  while their leases are live. Two worker processes on one machine must set distinct claimants (D20).
- One clock for every store-facing decision: due-ness, misfire, lease expiry, run and audit
  timestamps are read from `ISchedulingClock` inside the deciding unit, after its row lock — never
  from the host clock (`DateTime.UtcNow`, `TimeProvider`) in the facades. The default is the
  database's clock; a `TimeProviderClock` makes several hosts skew-sensitive again (D23).
- All times are UTC and say so: the SQLite sessions stamp `DateTimeKind.Utc` on read, matching
  Postgres `timestamptz`. Do not regress this — the two providers must agree.

## Conventions

Tabs; block-scoped namespaces; spaces inside parens `Method( arg )`; `.ConfigureAwait( false )`
in library code; `[DataContract]` records with the `#if NET5_0_OR_GREATER init/set` accessor
pattern; data access via `UnitsOfWork`: `ApplicationDataProvider.ReadAsync/WriteAsync( ( session,
context ) => …, context: new { … } )`, which are `ExecuteAsync`/`PerformAsync` in a `RequiresNew` scope.
`ReadAsync` runs at RepeatableRead, for pure reads; `WriteAsync` runs at ReadCommitted, for every
unit that writes. A write unit that acts on what it read locks the row first with a `RowLock` mode (a storage primitive; the caller picks it). `Wait` — Postgres `select … for update` — for every transition but one: the claim's per-candidate lock is `SkipLocked` — `for update skip locked` — which reads a held row as null, so the claim moves to the next candidate instead of queuing (D23, #14). Never skip anywhere else: a held row would read as "not held" or "missing" and a renewal, completion or management write would wrongly abandon or refuse. SQLite maps both to no clause: its `IMMEDIATE` write unit already serializes writers, so there is no row contention to wait on or skip. Nothing in
the domain layer begins or commits a transaction. **SQLite enlists itself** (the Pug.Sal pattern):
Microsoft.Data.Sqlite ignores `TransactionScope`, so its `DapperData` enlists every session created in
a scope. A write unit begins `IMMEDIATE`, taking the write lock up front, and a read unit begins
deferred; both commit in phase one. Postgres must not copy this: Npgsql enlists natively. SQL lives once in `DapperDataSessionBase`; a provider overrides only dialect hooks (never regenerate a provider by `sed`). Hosts on SQLite need WAL mode and must apply `GetSchemaUpgradeScripts()` in group order.
Contracts multi-target `net6.0;net8.0;net10.0` (the two `.WebApi` packages are `net8.0;net10.0`, D28),
implementations `net10.0`; the two API packages (`.WebApi`, `.Worker.WebApi`) `net8.0;net10.0`. Tests: xUnit, per-fixture databases migrated by DbUp from
`GetSchemaUpgradeScripts()`; Moq only for `ISecurityManager`. Store-backed suites are abstract
generics over `IScheduleDatabaseFixture` with a `Sqlite…` and a
`Postgres…` subclass each (Postgres: one shared Testcontainers server, a database per fixture);
the HTTP/API suites run on SQLite only. Test helpers touch the store through `UnitsOfWork`. The facades under test take a
`TimeProviderClock( ManualTimeProvider )`, and lease lapse is simulated by advancing it past
`LeaseDuration`; the database clock has its own tests (`SchedulingClockTests`, and a skewed-host
claim in `ExecutionCoordinatorTests`). DbUp sorts
scripts by name, so fixtures pass `RunGroupOrder` to keep the version/sequence order.

## Workflow requirement

Work proceeds in sub-phases with a **pause for user verification after every sub-phase**.
Current status and next steps: end of `docs/DECISIONS.md`.
