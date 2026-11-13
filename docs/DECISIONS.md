# Pug.Scheduling — Design Decisions

Decision log for the scheduled-report-generation initiative (approved plan:
`~/.claude/plans/consider-the-following-requirements-rustling-eagle.md`). Each entry records the
decision and the reasoning, so work can resume without the original conversation.

## Context

Customers of the user's .NET microservice SaaS want reports generated on a schedule and emailed.
On-demand report generation already exists in the SaaS. Agreed shape (2026-08-19/20):

- **Hybrid landing**: generic scheduling (`Pug.Scheduling`, this repo) and generic report
  definition/configuration (`Pug.ReportConfigurations`, Phase 2, not yet started) as reusable
  Pug.* NuGet packages; report rendering, the `ScheduledReportJobHandler`, and email sending stay
  app-specific in the SaaS.
- **No `Pug.Courier`**: a standalone delivery framework was explicitly rejected — `IEmailSender`
  is an abstraction in the same domain as `ScheduledReportJobHandler` (email dispatch is that
  handler's concern). MailKit SMTP is the intended v1 implementation.
- **Report model**: static catalog + user criteria now (`ReportDefinition` with JSON-Schema
  `ParameterSchema`, kind-versioned opaque `Specification` — `native/1` now, `designer/1` later);
  forward-compatible with a user-facing designer.

## D1 — Own DB-backed scheduler; Quartz.NET / Hangfire rejected

Schedules must carry tenant (`Domain`), owner principal, and arbitrary metadata as first-class,
queryable data. In Quartz these live in unqueryable `JobDataMap` blobs, forcing a parallel app
table (two sources of truth), and Quartz cannot be decomposed into the ecosystem's
`Models/Abstractions/Data.*/WebApi/Client` package shape. The claiming/misfire/retry core is
small and fully testable; `Pug.Corea.FetchWorker` proves the worker pattern in-ecosystem.

## D2 — Composed entity model over a single `schedule` table

`ScheduleInfo { Identifier, Definition, Owner, State, ExecutionLease, Registration, LastUpdate }`
(user-specified composition). `Definition` = user-authored (domain, name, jobType, payload JSON,
recurrence, policies); `State` = scheduler-maintained (enabled, paused, nextRun, runRequested,
attempt, lastRun summary); `Registration`/`LastUpdate` are Pug.Effable `ActionContext<Reference>`
(Corea `RepositoryInfo` precedent). One `schedule` table whose column groups map 1:1 to the model
sections — user explicitly kept state in the same table ("nothing wrong with that") but wanted
the model sectioned.

## D3 — Owner is not part of the Definition (user directive)

`Owner : Reference` sits on `ScheduleInfo`; `RegisterAsync( definition, owner = null )` defaults
it to the current security principal. Immutable thereafter (not updatable because it simply is
not in the definition). Owner rule (`SchedulerBase.IsCurrentUser`): matches only
`Owner.Type == "USER" && Owner.Identifier == CurrentUserIdentifier` (user tightened the Type
check). The rule covers **creating one's own schedules** (no ACL grant needed) and view/modify/
delete of owned schedules; registering on behalf of another principal requires the CREATE grant;
all other access falls through to Pug.Authorized ACLs with domain-qualified `NounQualifier`.
Security constants (`SecurityObjectTypes`, `SecurityOperations`) live in **Abstractions** — user
overrode the Corea precedent (core project): consumer-usable constants belong with abstractions.

## D4 — Role separation: IScheduler configures, IExecutionCoordinator executes

`IScheduler` is configuration/observation only — run-now never executes anything, it calls
`IDataSession.RequestRunAsync` (moves `NextRun` + raises `RunRequested`). Resume only lifts the
pause; elapsed occurrences are evaluated against the misfire policy by the coordinator at claim.

## D5 — Workers never touch the data backend (user directive)

Regardless of hosting model, workers poll/claim/heartbeat/report exclusively through
`IExecutionCoordinator`:

```csharp
Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType ); // null = none due
Task<bool> RenewClaimAsync( string run, string claimant, int worker );
Task ReportCompletionAsync( string run, string claimant, int worker, string result, JobOutcome outcome );
```

`ClaimedJob { Context, Timeout, ClaimExpiry }`. A claim returns **one** job (the worker runs one
at a time; a batch claim let one slow job idle the rest of the batch); `jobType` null means any
type. Caller identity (`claimant`, `worker`) is D16's. Due-ness, claim semantics, misfire, retry,
and next-occurrence computation are all invisible to workers. An externally hosted worker binds
the same interface to a transport client (D17). The schedule store's
`GetDueSchedulesAsync( dueBy, jobType, maximumSchedules )` is a plain read — concurrent
coordinators race for leases. The coordinator reads a window of
`ExecutionCoordinatorOptions.ClaimCandidates` (default 10) due schedules and returns the first
whose lease it acquires and that is not disposed of (misfire SKIP, stale read): losing the race for
the oldest due schedule moves on to the next instead of coming back empty while work is due. The
job-type filter is a separate SQL statement, not `@jobType is null or …` (an untyped null
parameter is rejected by Npgsql), served by the schema-v2 partial index
`ixScheduleJobTypeDue on schedule(jobType, nextRun)`.

## D6 — Lease claiming is its own abstraction with its own store (user directive)

`IExecutionLeaseProvider { TryAcquireAsync( schedule, claimant, expiry ), RenewAsync,
ReleaseAsync, GetAsync }` in Abstractions. Implementation (in-memory, database — same or
different DB — or remote lock service) is transparent to the coordinator; lease state is **never
stored with the schedule** (no lease columns; `ScheduleInfo.ExecutionLease` is composed from the
provider at read time). `Pug.Scheduling.Leases` implements it over its own
`Leases.Data.Abstractions` store (Postgres/SQLite: single-statement conditional upsert
`insert … on conflict(schedule) do update … where expired-or-own`, so exclusivity is the store's
atomicity and any number of coordinators race safely). An InMemory provider was considered and
**rejected as not required**. Leases are deliberately short (default 2 min) and heartbeat-renewed
(worker default 30 s) rather than sized to job timeouts.

## D7 — Third-party handler defensiveness (user directive)

Handlers/dispatchers are "third-party" code. `JobOutcome { Successful, Details, Error }` (names
user-specified): handlers report failure by returning unsuccessful **or** throwing — the worker
catches everything, materializes `{ Successful=false, Error }`, treats a null return as
unsuccessful, and never trusts cancellation (lease expiry bounds runaways; an overrunning handler
risks concurrent re-execution). `ScheduleRun` embeds `Outcome : JobOutcome` and **keeps** the
worker-classification `Result` string (`RunResults`): TIMED_OUT and SKIPPED runs never reach the
handler, so they have no handler outcome.

## D8 — Task<T> + documented exceptions, not Pug.Lang Result

`Pug.Lang.Result<T>` is a plain value wrapper (no error channel), and `ICorea` — the newest
precedent — uses `Task<T>` with exceptions (`UnknownScheduleException`, `UnknownRunException`,
`NotAuthorized`, `ArgumentException` for validation). Followed the precedent.

## D9 — Retry identity and at-least-once bookkeeping

Side-effect idempotency keys on `(Schedule, ScheduledFor)`: retries re-execute the **original**
occurrence (carried via `IDataSession.GetLatestRunAsync`), backoff = `Backoff * 2^(retry-1)` from
now; exhausted retries record terminal failure and advance. A crashed worker's unfinished run is
**adopted** on re-claim (same run row) instead of inserting a duplicate — which keeps the
`(schedule, scheduledFor, attempt)` unique index honest under at-least-once. Reporting for a
deleted schedule surfaces as `UnknownRunException` (delete removes run history; a mid-flight
run's completion is recorded nowhere, per `IScheduler.DeleteAsync` contract).

## D10 — MANUAL trigger is first-class (user directive)

`ScheduleState.RunRequested` (+ `runRequested` column): raised by run-now via `RequestRunAsync`,
consumed **transactionally by the claim that honors it** (`ClearRunRequestAsync` with the run
insert), defensively cleared by `UpdateNextRunAsync` and `RecordOccurrenceCompletionAsync`.
Trigger precedence at claim: MANUAL > RETRY > MISFIRE > SCHEDULE. A manual request during retry
backoff accelerates the pending attempt but keeps the original occurrence. A run-now raised while
the same schedule is mid-execution is treated as satisfied by that execution (completion clears
the marker) — deliberate.

## D11 — Misfire policies

Occurrence is misfired when overdue beyond `MisfirePolicy.Threshold` (default 5 min).
`FIRE_ONCE` (default): coalesce — execute once (trigger MISFIRE), next occurrence computed from
now. `SKIP`: record a SKIPPED run (audit trail), advance, release — no execution. `RUN_ALL`:
execute, and completion computes the next occurrence from the *executed* occurrence, naturally
catching up one by one.

## D12 — Time and storage conventions

Cronos for cron math, evaluated in the recurrence's IANA `TimeZone` (DST: skipped times jump
forward); interval mode is UTC arithmetic from the previous occurrence; `NotBefore`/`NotAfter`
clamp/exhaust. Everything persisted/compared in UTC. Durations stored as whole-second bigint
columns (no TimeSpan handlers); SQLite DateTimes as invariant ISO-8601 TEXT (chronologically
comparable); `ActionContext`/`RunSummary`/`JobOutcome` as JSON text columns via
`JsonTypeHandler<T>`. Run trigger column is `runTrigger` (`trigger` is reserved in Postgres).
Deletes are explicit (runs then schedule) rather than relying on FK cascade enforcement.

## D13 — Job types: schedulable vs executable

`IJobTypeRegistry` (Abstractions) separates what is *schedulable* at the API host from what is
*executable* where (handlers may live in other processes). DI: `AddScheduledJobHandler<T>(jobType)`
(keyed scoped, resolved per run by `InProcessJobDispatcher` from a fresh DI scope),
`AddJobPayloadValidator<T>(jobType)` (keyed singleton), `AddSchedulableJobType(jobType)` for
remote-execution deployments. Unregistered job types are rejected at registration.

## D14 — Dispatch transport

`IScheduledJobDispatcher` is the long-lived transport strategy (one per worker host, not per
job): in-process v1 (no broker exists in the ecosystem; DB lease already gives at-least-once).
Future webhook shape: standalone scheduler host (WebApi + worker) with a `WebhookJobDispatcher`
POSTing `ScheduledJobContext` to per-job-type endpoints — needs service-to-service auth +
owner-identity conveyance + a completion protocol; deliberately deferred.

## D15 — WebApi surfaces

Three separately mapped surfaces, each carrying **its own route prefix** rather than relying on
the host to supply one: a host's routes cannot be assumed to be the scheduler's alone, and the
scheduler host serves more than one scheduling surface at once (user directive).

| Extension | Package | Routes |
|---|---|---|
| `MapSchedulingEndpoints()` | `Pug.Scheduling.WebApi` | `GET/POST schedules`, `GET/PUT/DELETE schedules/{identifier}`, `POST schedules/{identifier}/pause|resume|run`, `GET schedules/{identifier}/runs` |
| `MapExecutionCoordinatorEndpoints()` | `Pug.Scheduling.WebApi` | `POST executionCoordinator/JobRunClaims`, `POST executionCoordinator/JobRuns/{run}/leaseRenewal?claimant=&worker=`, `POST executionCoordinator/JobRuns/{run}/completion?claimant=&worker=` |
| `MapSchedulerWorkerEndpoints()` | `Pug.Scheduling.Worker.WebApi` | `GET worker`, `POST worker/pause|resume` |

- **Coordinator endpoints are the out-of-process worker's transport** (user directive): the wire
  contract is `IExecutionCoordinator` unchanged, so D5 holds over HTTP — a remote worker still
  never reaches the data backend, and due-ness/misfire/retry/next-occurrence stay behind the
  facade. Request bodies `JobRunClaimRequest { Claimant, Worker, JobType }` (`JobType` optional) and
  `JobRunCompletion { Result, Outcome }` live in **`Pug.Scheduling.WebApi.Models`** (see D17): HTTP
  surface contracts, not domain models — nothing but the transport uses them — and deliberately
  outside the ASP.NET-referencing WebApi packages so a worker host's client never pulls those in. A claim answers `200` with
  the `ClaimedJob`, or `204` when nothing is due (the client maps it back to null). Renewal answers
  `200` with the coordinator's boolean: a lost claim is an expected heartbeat outcome, not a request
  failure. Renewal and completion identify the caller by query `claimant` and `worker`; the worker
  number is parsed by the handler, so an omitted or malformed one is a structured 400 like any
  other.
- **Worker management endpoints are their own package** `Pug.Scheduling.Worker.WebApi` (user
  directive): a worker hosted out-of-process reaches the scheduler solely through the coordinator
  transport and must not have to reference packages it does not need.
- **Authorization stays with the facades** (see D16). Schedule and coordinator endpoints perform
  none of their own; they require an authenticated caller and map outcomes: `NotAuthorized` → 403,
  `UnknownScheduleException`/`UnknownRunException` → 404, `ArgumentException` → 400
  `BadRequestResultInfo`. Worker management endpoints remain the one exception —
  `ISchedulerWorker` is a local control surface with no facade behind it — so they check
  `WorkerSecurityOperations` on `SecurityObjectTypes.SchedulerWorker`, qualified by claimant, with
  no owner exemption.
- **`ISchedulerWorker` moved to Abstractions and `WorkerInfo` to Models** (from
  `Pug.Scheduling.Worker`): both WebApi packages multi-target `net6.0;net8.0;net10.0` and cannot
  reference the net10.0-only worker implementation. The interface stays synchronous — it manages
  the locally hosted instance, not a remote one.
- **Owner is conveyed as an `ownerType`/`ownerIdentifier` query pair** (Groups' `Subject`
  convention) rather than a new request contract; supplying one half without the other is a 400.
- **Run-now answers 202, not 200**: the request only brings the occurrence forward for a worker
  to claim.
- Query parameters the handler validates are declared nullable so omission reaches the handler
  and yields a structured `BadRequestResultInfo`, instead of model binding failing with an empty
  400 body.

## D16 — The execution coordinator authorizes its own callers (user correction)

Authorization for `IExecutionCoordinator` lives **inside `ExecutionCoordinator`**, not in the
coordinator endpoints where it was first placed. Keeping it in the facade means it cannot be
bypassed by binding the coordinator differently — the HTTP layer would only have guarded the one
transport it happens to serve. Each operation requires its `CoordinatorSecurityOperations` grant
on `SecurityObjectTypes.ExecutionCoordinator` qualified by the **claimant identity being acted
as** (`ResourceQualifiers.ExecutionCoordinator`), so one worker's credentials cannot claim, renew,
or complete runs as another. There is no owner rule: execution is an operator concern, not the
schedule owner's.

**Claimant is the group identity; the lease holder is the worker** (rework sub-phase A). Every
coordinator call carries `claimant` and `worker` (a number, 1 or greater). Grants are qualified by
the claimant alone, so every worker of a group shares them; the lease is held as
`"{claimant}/{worker}"`, composed inside `ExecutionCoordinator` and handed to
`IExecutionLeaseProvider` unchanged, so two workers of one group never share a claim. `/` is
therefore reserved: a claimant containing one, or a worker below 1, is an `ArgumentException`
(400 over HTTP). A restarted worker presenting the same claimant and number is the same holder: the
lease store lets a holder re-take its own live lease, and the coordinator adopts the unfinished run
(D9) rather than recording a duplicate — restart takeover needs no lease-provider change. The
corollary is that worker numbers must be unique within a claimant across live processes.

`ExecutionCoordinator` logs its transitions through `ILogger<ExecutionCoordinator>` with structured
`{Claimant}`, `{Worker}`, `{Schedule}`, `{Run}`, `{ScheduledFor}`, `{Attempt}`: claims, adoptions,
completions, retries and misfire skips at Information; lost lease races, no due work and computed
next occurrences at Debug; authorization denials, renewals or completions from a worker that no
longer holds the claim, and stale due candidates at Warning; a failure preparing a claimed job at
Error, before its lease is released and the error rethrown. A completion from a worker that no
longer holds the claim is still recorded, as before — the warning makes the possible double
execution visible, it does not refuse it.

**A call arriving with no principal at all is trusted and skips the check** (user directive).
Every endpoint in front of the coordinator requires an authenticated caller before dispatch, so a
call that crossed a transport always carries a principal; the absence of one identifies an
in-process caller. An in-process `SchedulerWorker` therefore needs no service identity and holds
no grants — its `BackgroundService` polls simply run outside any authenticated request. Hosts
should note the corollary: an `ISecurityManager` that manufactures a non-null anonymous or system
principal for background contexts will be *checked*, not skipped, and must then be granted.
(`Scheduler` is unaffected and still requires a principal — it derives schedule `Owner` and audit
contexts from it.)

## D17 — API clients

`Pug.Scheduling.Client` implements **both** facades over the 1e routes: `SchedulerClient`
(`IScheduler`) and `ExecutionCoordinatorClient` (`IExecutionCoordinator`), the latter being what an
out-of-process worker host binds so `SchedulerWorker` runs unchanged against a remote scheduler
(user directive). Built on `Pug.HttpApiClient` 0.6.0; the package references only Abstractions, so
a worker host pulls in no ASP.NET-referencing package.

- **Substitutability is the contract.** The clients reproduce the facades' documented exceptions,
  reversing the endpoints' mapping: 403 → `NotAuthorized`, 404 → `UnknownScheduleException` /
  `UnknownRunException`, 400 → `ArgumentException` rebuilt from the `BadRequestResultInfo` body.
  401 stays the transport's `AuthenticationException`: no facade contract covers failing to
  authenticate.
- **`Pug.Scheduling.WebApi.Models` holds every HTTP surface contract** (user directive):
  `BadRequestResultInfo`, `JobRunClaimRequest`, `JobRunCompletion`. Both the `.WebApi` projects that
  render them and the `.Client` projects that read them back may reference it. It sits apart from
  the domain models — these describe the transport, and nothing in the domain uses them — and apart
  from the ASP.NET-referencing WebApi packages, since a worker host must not pull those in. It
  references Models only for the domain types the contracts carry (`JobOutcome`).
- **Renewal returns `false`, not an exception**, so a worker can tell "no longer mine" from "the
  call did not get through".
- No worker-management client: `ISchedulerWorker` is synchronous and documented as local control
  of the in-process instance; a remote one would need a different, async contract.
- No DI extensions in the client package — binding is one registration, and keeping
  `Microsoft.Extensions.DependencyInjection` out keeps the package usable from a bare worker host.
  The README carries the wiring.
- Workaround: `Pug.HttpApiClient` 0.6.0 aggregates the query collection without a seed, so an
  **empty** (non-null) query dictionary throws where null is handled. The client passes null when
  it has nothing to ask.

## D18 — SQLite timestamps are stamped UTC on read (defect found in 1f)

The SQLite stores keep timestamps as ISO-8601 text without an offset, so the provider returned them
as `DateTimeKind.Unspecified` while the Postgres `timestamptz` columns return `Utc` — the two
providers disagreed, and a consumer calling `ToLocalTime()` on a schedule's `NextRun` would have
shifted a value that was already UTC. Both SQLite sessions now stamp `DateTimeKind.Utc` on read
(schedule `NotBefore`/`NotAfter`/`NextRun`, run `ScheduledFor`/`Started`/`Finished`, lease
`Expiry`). JSON columns were never affected — `System.Text.Json` round-trips the `Z`.

Surfaced by the client round-trip tests, which assert on `Kind` because the wire format makes the
difference visible; the in-process tests had only ever compared instants.

## Verification conventions

Completion gate: `dotnet build Pug.Scheduling.slnx -warnaserror -m:1` clean **and**
`dotnet test tests/Pug.Scheduling.Tests` green. Tests run against real SQLite stores (per-fixture
temp DB, DbUp-migrated from `GetSchemaUpgradeScripts()`, WAL + busy_timeout). **No Docker on this
machine**: Postgres providers (schedule + lease stores) are build-verified only — SQL differs
from tested SQLite only in DDL types and boolean predicates; run Testcontainers races in CI.

## Phase status (per approved plan; pause for user review after every sub-phase)

- **1a Models + Abstractions — DONE**  · **1b Data.Abstractions — DONE** · **1c Leases — DONE**
  · **1d implementation (core/data/worker/DI + 44 tests) — DONE**
  · **1e WebApi (schedule, execution coordinator and worker endpoints, 29 endpoint tests; 75
  total) — DONE**
  · **1f `.Client` (both facades), packaging metadata, README — DONE (88 tests)**
- **Execution rework** (plan: single-job workers, no dispatcher, worker groups):
  **A coordinator contract (`claimant` + `worker`, one job per claim, job-type filter, candidate
  window, schema v2 index) and coordinator logging — DONE (107 tests)**; B job-handler projects /
  factory registry / no dispatcher, C worker groups and per-worker management — pending.
- **Phase 1 COMPLETE.** Carry-overs: Postgres Testcontainers races in CI; webhook dispatch (D14)
  still deferred.
- **Phase 2** `Pug.ReportConfigurations` (sub-phases 2a–2e, same shape as this repo).
- **Phase 3** SaaS integration (handler + `IEmailSender`/MailKit + `IEmailSenderProvider`,
  recipient policy, output store, link-first delivery ≤ 5 MB attachments, owner impersonation at
  generation time — runs execute as the stored owner, never system).
- **Phase 4** hardening + Corea-style demo host.
