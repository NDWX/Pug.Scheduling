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
delete of owned schedules; registering on behalf of another principal requires the CREATE_ON_BEHALF grant (D26);
all other access falls through to Pug.Authorized ACLs with domain-qualified `NounQualifier`.
Amended (#32): the `LIST` exemption follows the same owner rule (the owner filter is `{ USER, caller }`), and the owner filter matches type and identifier.
Security constants (`SecurityObjectTypes`, `SecurityOperations`) live in **Abstractions** — user
overrode the Corea precedent (core project): consumer-usable constants belong with abstractions.

## D4 — Role separation: IScheduler configures, IExecutionCoordinator executes

`IScheduler` is configuration/observation only — run-now never executes anything, it raises the
`RunRequested` marker (originally `IDataSession.RequestRunAsync`, which also moved `NextRun`; D10 and
D23: the marker never moves `NextRun`). Resume only lifts the
pause; elapsed occurrences are evaluated against the misfire policy by the coordinator at claim.

## D5 — Workers never touch the data backend (user directive)

Regardless of hosting model, workers poll/claim/heartbeat/report exclusively through
`IExecutionCoordinator`:

```csharp
Task<ClaimedJob> ClaimDueJobAsync( string claimant, int worker, string jobType ); // null = none due
Task<bool> RenewClaimAsync( string run, string claimant, int worker );
Task<bool> AbandonClaimAsync( string run, string claimant, int worker );  // D27
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

**Amended (#10, D23/D25):** the store read is now `GetDueCandidatesAsync( now, holder, jobType, max )`,
a coarse pre-filter that returns identifiers; exclusivity is the schedule row's lock, not a lease
race, and the candidate loop isolates failures (D23). "One job at a time" is "one *attended* job at
a time": a handler that ignores cancellation is detached and the worker moves on (D25). The
indexes are the schema-v3 ones (`activeRun is null` partial predicates).

**Amended (#35, D29):** `ClaimedJob` gains `ClaimDuration` (the coordinator's `LeaseDuration`), which
the worker times its claim by; `ClaimExpiry` stays informational.

## D6 — Lease claiming is its own abstraction with its own store (user directive) — SUPERSEDED by D23

Historical: the lease provider and the `Pug.Scheduling.Leases*` projects were removed in #10; the
lease now lives on the schedule row (D23).

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

Handlers and their factories (D19) are "third-party" code. `JobOutcome { Successful, Details, Error }` (names
user-specified): handlers report failure by returning unsuccessful **or** throwing — the worker
catches everything, materializes `{ Successful=false, Error }`, treats a null return as
unsuccessful, and never trusts cancellation (lease expiry bounds runaways; an overrunning handler
risks concurrent re-execution). A factory that throws or returns null, or a missing factory, is
likewise an unsuccessful outcome; handler disposal failures are logged and never change it.
`ScheduleRun` embeds `Outcome : JobOutcome` and **keeps** the
worker-classification `Result` string (`RunResults`): TIMED_OUT and SKIPPED runs never reach the
handler, so they have no handler outcome.

**Amended (#10, D25):** "never trusts cancellation" now has a mechanism: a handler that does not
finish within `CancellationGrace` of being cancelled is detached, and the worker moves on. A detached
handler may still be running when its occurrence is retried (or adopted by another worker), so it
may overlap its own retry; handlers must key side effects on `(Schedule, ScheduledFor)`.

## D8 — Task<T> + documented exceptions, not Pug.Lang Result

`Pug.Lang.Result<T>` is a plain value wrapper (no error channel), and `ICorea` — the newest
precedent — uses `Task<T>` with exceptions (`UnknownScheduleException`, `UnknownRunException`,
`NotAuthorized`, `ArgumentException` for validation). Followed the precedent.

## D9 — Retry identity and at-least-once bookkeeping

Side-effect idempotency keys on `(Schedule, ScheduledFor)`: retries re-execute the **original**
occurrence (originally carried via `IDataSession.GetLatestRunAsync`; see the amendment), backoff = `Backoff * 2^(retry-1)` from
now; exhausted retries record terminal failure and advance. A crashed worker's unfinished run is
**adopted** on re-claim (same run row) instead of inserting a duplicate — which keeps the
`(schedule, scheduledFor, attempt)` unique index honest under at-least-once. Reporting for a
deleted schedule surfaces as `UnknownRunException` (delete removes run history; a mid-flight
run's completion is recorded nowhere, per `IScheduler.DeleteAsync` contract).

**Amended (#10, D23):** the retry's occurrence is carried on the schedule row (`occurrence`, non-null
only while `attempt > 0`), so no claim infers it from the latest run row and `GetLatestRunAsync` is
gone. Adoption is decided on the locked row (`OccurrenceState.Classify`: adopt precedes every other
rule), not by matching `(scheduledFor, attempt)` against a run read.

**Amended (#45):** a claim hands out the run as stored, not as composed. `StartRunAsync` reads the run
back after inserting it, as `AdoptRunAsync` does, so the first claim and any adoption hand out the
same `ScheduledFor`. A manual run's occurrence is the clock's now, and a store may keep it at a coarser
precision (Postgres: microseconds) than a host clock produces (100 ns ticks).

## D10 — MANUAL trigger is first-class (user directive)

`ScheduleState.RunRequested` (+ `runRequested` column): raised by run-now via `RequestRunAsync`,
consumed **transactionally by the claim that honors it** (`ClearRunRequestAsync` with the run
insert), defensively cleared by `UpdateNextRunAsync` and `RecordOccurrenceCompletionAsync`.
Trigger precedence at claim: MANUAL > RETRY > MISFIRE > SCHEDULE. A manual request during retry
backoff accelerates the pending attempt but keeps the original occurrence. A run-now raised while
the same schedule is mid-execution is treated as satisfied by that execution (completion clears
the marker) — deliberate.

**Amended (#10, user directive change; D23):** the last sentence is withdrawn. It let a run-now move
`nextRun` while a run was active, so the occurrence was duplicated after a crash.
- **Run-now is refused while a run is active, live or lapsed** (awaiting adoption):
  `RunInProgressException { Schedule, Run, Holder, LeaseExpiry }`, 409 over HTTP with a
  `RunInProgressResultInfo` body (`Pug.Scheduling.WebApi.Models`); the client rebuilds the exception.
- **The marker never moves `nextRun`.** `RequestRunAsync`, `ClearRunRequestAsync` and the defensive
  clearing are gone; the claim that honors the request clears it in its own unit.
- **Manual runs are not retried**: a failed manual run leaves the occurrence grid alone.
- **A manual run during retry backoff accelerates the retry**: it carries the retry's attempt and
  occurrence and counts as that retry.
- **RUN_ALL keeps its backlog** across a manual run; **FIRE_ONCE and SKIP coalesce** an occurrence
  already due when the run started (rules in `CompletionRules`).

## D11 — Misfire policies

Occurrence is misfired when overdue beyond `MisfirePolicy.Threshold` (default 5 min).
`FIRE_ONCE` (default): coalesce — execute once (trigger MISFIRE), next occurrence is the first grid
slot after now (`Next( r, T, now )`, phase kept). `SKIP`: record a SKIPPED run (audit trail), advance, release — no execution. `RUN_ALL`:
execute, and completion steps one slot from the *executed* occurrence (`Next( r, T, T )`), naturally
catching up one by one.

**Verified/amended (#10, sub-phase C):** the baselines are applied by `CompletionRules.NextOccurrence`
on completion and on a SKIP, and an interval's grid keeps its phase (D12), so FIRE_ONCE/SKIP coalesce
onto the original grid rather than drifting from "now". A manual run does not advance RUN_ALL's
backlog (D10).

## D12 — Time and storage conventions

Cronos for cron math, evaluated in the recurrence's IANA `TimeZone` (DST: skipped times jump
forward); interval mode is UTC arithmetic from the previous occurrence; `NotBefore`/`NotAfter`
clamp/exhaust (amended in #10, below). Everything persisted/compared in UTC. Durations stored as whole-second bigint
columns (no TimeSpan handlers); SQLite DateTimes as invariant ISO-8601 TEXT (chronologically
comparable); `ActionContext`/`RunSummary`/`JobOutcome` as JSON text columns via
`JsonTypeHandler<T>`. Run trigger column is `runTrigger` (`trigger` is reserved in Postgres).
Deletes are explicit (runs then schedule) rather than relying on FK cascade enforcement.

**Amended (#10, sub-phase C):** `RecurrenceCalculator` exposes `GetFirstOccurrence( r, now )` and
`GetNextOccurrence( r, previous, after )`. Cron: the next occurrence strictly after `after`.
Interval: `previous + k·I` for the smallest `k >= 1` landing after `after`, so the grid keeps its
phase instead of drifting. First occurrence of an interval: with `NotBefore` set, the smallest `NotBefore + k·I`
(`k >= 0`) at or after `now` (`NotBefore` anchors the grid); without it, `now + I` (previously
`NotBefore + I`). `NotBefore`/`NotAfter` clamp
every result. Registration and a recurrence change use `GetFirstOccurrence`, but a definition update
changes `nextRun` only when the schedule is idle (`attempt = 0`, no run active) and the recurrence
changed; during a run it leaves `nextRun` alone and the completion applies the new recurrence
(`DefinitionRules.NextRunAfterUpdate`).

**Amended (#33):** every schedule duration (`Interval`, `Threshold`, `Backoff`, `Timeout`) must be
whole seconds, because the stores keep whole-second columns and truncated a fraction silently (a
sub-second interval became zero and divided by zero at completion). `Scheduler` and
`RecurrenceCalculator.Validate` reject a fraction with `ArgumentException` (400), through one shared
rule (`DurationRules`). Minimums: interval and timeout at least 1 s (a zero timeout still means
"omitted", replaced by `DefaultTimeout`, so a stored timeout is never zero), backoff not negative.
`SchedulerOptions.MinimumMisfireThreshold` (default 15 s) is a floor on every `MisfirePolicy.Threshold`
and on the default; a smaller one would turn every claim into a misfire. The `Scheduler` constructor
validates the options (whole seconds, defaults within their minimums). `RecurrenceCalculator` throws
`InvalidOperationException` for a non-positive interval read from a row written before validation.

**Amended (#41):** a specified `Timeout` must be at least **5 s** (`DurationRules.MinimumTimeout`),
and so must `SchedulerOptions.DefaultTimeout`; zero still means "omitted". A shorter timeout leaves a
handler too little time once worker overheads (handler creation, claim round trips, cancellation)
are counted. The floor is fixed, not host-configurable. It applies when a definition is registered or
updated; stored schedules are not rewritten.

**Amended (#47):** `Recurrence.TimeZone` must be an IANA identifier; `RecurrenceCalculator.Validate`
refuses a zone whose `TimeZoneInfo` is not IANA (`HasIanaId`), so a Windows ID is a 400 at
registration and update. The zone is resolved again at every completion, on the coordinator's host,
and IANA IDs are what every platform's tz database knows natively; a Windows ID resolves on Linux only
through ICU's conversion. Hosts need ICU and an up-to-date tz database (Linux: `libicu`, `tzdata`, no
`InvariantGlobalization`); one that cannot resolve a schedule's zone fails every completion of it.
This closes the last validated-data path of #43's failing-completion loop.

## D13 — Job types: schedulable vs executable

`IJobTypeRegistry` (Abstractions) separates what is *schedulable* at the API host from what is
*executable* where (handlers may live in other processes). Scheduler DI
(`Pug.Scheduling.DependencyInjection`): `AddJobPayloadValidator<T>(jobType)` (keyed singleton) and
`AddSchedulableJobType(jobType)`, both of which mark the type schedulable. Worker DI
(`Pug.Scheduling.Worker.DependencyInjection`, D19): `AddScheduledJobHandler<T>(jobType)` and
`AddScheduledJobHandlerFactory<T>(jobType)`, which register how a type is *executed* and no longer
mark it schedulable — an in-process host calls `AddSchedulableJobType` as well. Unregistered job
types are rejected at registration.

## D14 — Dispatch transport (SUPERSEDED by D19)

`IScheduledJobDispatcher` is the long-lived transport strategy (one per worker host, not per
job): in-process v1 (no broker exists in the ecosystem; DB lease already gives at-least-once).
Future webhook shape: standalone scheduler host (WebApi + worker) with a `WebhookJobDispatcher`
POSTing `ScheduledJobContext` to per-job-type endpoints — needs service-to-service auth +
owner-identity conveyance + a completion protocol; deliberately deferred.

## D19 — No dispatch layer; handler factory registry (execution rework, sub-phase B)

Supersedes D14. There is no `IScheduledJobDispatcher`: running a job somewhere else (e.g. calling a
webhook) is the **handler's** responsibility, so a webhook is just a handler whose `ExecuteAsync`
makes the call. This leaves one extension point instead of two.

- **Factory registry keyed by job type.** `IScheduledJobHandlerFactoryRegistry.GetFactory( jobType )`
  returns the `IScheduledJobHandlerFactory` for the type, or null (worker reports `FAILED`, "No
  handler registered for job type"; retried per policy).
- **The factory takes the context.** `Create( ScheduledJobContext )` lets a factory pick or configure
  a handler per run (tenant, owner, payload).
- **The handler receives the timeout.** `ExecuteAsync( context, timeout, cancellationToken )` —
  the schedule's execution timeout, which the worker still enforces; handlers calling elsewhere can
  pass it on.
- **The worker disposes handlers** after every run (`IAsyncDisposable`, else `IDisposable`),
  whatever the outcome; dispose failures are logged and swallowed.
- **Two new projects.** `Pug.Scheduling.JobHandler.Abstractions` (multi-target, depends on Models
  only: all a handler author needs) and `Pug.Scheduling.Worker.DependencyInjection` (net10.0;
  `AddSchedulingWorker` — `AddSchedulingWorkers` since D20 — `AddScheduledJobHandler<T>`,
  `AddScheduledJobHandlerFactory<T>`). The
  worker host's DI is separate from the scheduler's because an out-of-process worker host must not
  reference `Pug.Scheduling`, the data stores or the lease stores. `AddScheduledJobHandler<T>` keeps
  one DI scope per run: its factory creates the scope and the returned handler wrapper disposes it.
- **Behaviour change:** handler registration no longer marks a job type schedulable; that bookkeeping
  stays in `Pug.Scheduling.DependencyInjection` (`AddSchedulableJobType`, `AddJobPayloadValidator`).
- Worker logging was completed (started/stopping, paused/resumed, claimed, finished, timed out,
  failures, dispose failures; Debug for empty polls and renewals).

## D15 — WebApi surfaces

Three separately mapped surfaces, each carrying **its own route prefix** rather than relying on
the host to supply one: a host's routes cannot be assumed to be the scheduler's alone, and the
scheduler host serves more than one scheduling surface at once (user directive). **Amended (D28):** the
segment is the default of a host-configurable base path, and each surface takes authorization policies.

| Extension | Package | Routes |
|---|---|---|
| `MapSchedulingEndpoints()` | `Pug.Scheduling.WebApi` | `GET/POST schedules`, `GET/PUT/DELETE schedules/{identifier}`, `POST schedules/{identifier}/pause|resume|run`, `GET schedules/{identifier}/runs` |
| `MapExecutionCoordinatorEndpoints()` | `Pug.Scheduling.WebApi` | `POST executionCoordinator/JobRunClaims`, `POST executionCoordinator/JobRuns/{run}/leaseRenewal?claimant=&worker=`, `POST executionCoordinator/JobRuns/{run}/abandonment?claimant=&worker=` (D27), `POST executionCoordinator/JobRuns/{run}/completion?claimant=&worker=` |
| `MapSchedulerWorkerEndpoints()` | `Pug.Scheduling.Worker.WebApi` | `GET workers`, `GET workers/{worker}`, `POST workers/{worker}/pause|resume` (D20) |

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
  transport and must not have to reference packages it does not need. They manage the workers
  hosted in the serving process through `ISchedulerWorkerRegistry`, addressing each by its
  host-wide worker number (D20): `GET workers` lists only the workers whose claimant the caller
  holds `GetInfo` for (200 with an empty array when none — the caller may be entitled to workers
  another host runs); `GET workers/{worker}` returns one `WorkerInfo`; `POST
  workers/{worker}/pause|resume` return the resulting `WorkerInfo`. An unknown number is 404 and a
  missing grant on that worker's claimant 403. The number comes before the grant check because the
  claimant the grant is qualified by is the worker's, so an authenticated caller can learn which
  numbers exist, nothing more.
- **Authorization stays with the facades** (see D16). Schedule and coordinator endpoints perform
  none of their own; they require an authenticated caller with a principal (D28, #34) and map outcomes: `NotAuthorized` → 403,
  `UnknownScheduleException`/`UnknownRunException` → 404, `ClaimNotHeldException` → 409 (completion
  only), `RunInProgressException` → 409 with a `RunInProgressResultInfo` body (run route only;
  amended in #10), `ArgumentException` → 400 `BadRequestResultInfo`. This mapping lives once, in `FacadeResults.TranslateAsync`, which wraps every
  facade call, so no route can miss one. Worker management endpoints remain the one exception —
  `ISchedulerWorker` is a local control surface with no facade behind it — so they check
  `WorkerSecurityOperations` on `SecurityObjectTypes.SchedulerWorker`, qualified by claimant, with
  no owner exemption.
- **`ISchedulerWorker` moved to Abstractions and `WorkerInfo` to Models** (from
  `Pug.Scheduling.Worker`): both WebApi packages multi-target `net6.0;net8.0;net10.0` and cannot
  reference the net10.0-only worker implementation. `ISchedulerWorkerRegistry` sits beside it for
  the same reason (D20). The interface stays synchronous — it manages
  the locally hosted instance, not a remote one.
- **Owner is conveyed as an `ownerType`/`ownerIdentifier` query pair** (Groups' `Subject`
  convention) rather than a new request contract; supplying one half without the other is a 400.
- **Run-now answers 202, not 200**: the request only raises a marker for a worker to claim. **Amended
  (#10):** it answers 409 `RunInProgressResultInfo` while a run is active (D10), and 400 for a
  definition whose `Timeout` exceeds `TimerBounds.Maximum` at registration/update (D25).
- Query parameters the handler validates are declared nullable so omission reaches the handler
  and yields a structured `BadRequestResultInfo`, instead of model binding failing with an empty
  400 body.

## D16 — The execution coordinator authorizes its own callers (user correction)

Authorization for `IExecutionCoordinator` lives **inside `ExecutionCoordinator`**, not in the
coordinator endpoints where it was first placed. Keeping it in the facade holds every transport
to the same grants — the HTTP layer would only have guarded the one transport it happens to serve.
(Every transport that presents a principal, that is: see the amendment below on calls without one.) Each operation requires its `CoordinatorSecurityOperations` grant
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
next occurrences at Debug; authorization denials, renewals from a worker that no longer holds the
claim, refused or lapsed-claim completions, and stale due candidates at Warning; a failure
preparing a claimed job at Error, before its lease is released and the error rethrown.

**A completion is accepted only if the reporter can be verified to hold the run.** *(Superseded in
#10 by D23: the authority is the schedule row's `holder`; `ScheduleRun.ClaimedBy` is audit only.
The rules below are history.)* The run records
its holder (`ScheduleRun.ClaimedBy`, `"{claimant}/{worker}"`), set when the run is inserted
(including a misfire-SKIP run) and transferred when a later claim adopts the unfinished run (D9).
`ReportCompletionAsync` then: refuses a run that is already finished (a duplicate report must not
re-complete it or advance the schedule again); accepts a report from the recorded holder even if
its lease has lapsed, since the run is still recorded as its own (logged as a warning); refuses a
report from anyone else; and, for a legacy row with no recorded holder, accepts only if the
current lease holder is the reporter. Every refusal logs a warning and throws
`ClaimNotHeldException` before any write (409 over HTTP, mapped back by the client). The
completing update repeats the check in the same statement (`where finished is null and claimedBy =
holder`), so an adoption that lands between the checks and the write still refuses the completion
instead of overwriting the adopter's run. `SchedulerWorker` already logs and swallows a failed
completion report.

**A call arriving with no principal at all is trusted and skips the check** (user directive).
Every endpoint in front of the coordinator requires an authenticated caller before dispatch, so a
call that crossed a transport always carries a principal; the absence of one identifies an
in-process caller. An in-process `SchedulerWorker` therefore needs no service identity and holds
no grants — its `BackgroundService` polls simply run outside any authenticated request. Hosts
should note the corollary: an `ISecurityManager` that manufactures a non-null anonymous or system
principal for background contexts will be *checked*, not skipped, and must then be granted.

**Amended (#34): the trust is kept, for in-process calls only.** Since D28 a host's policies had
replaced the default policy, so a permissive policy or `DefaultPolicy`, `.AllowAnonymous()` on the
returned group, or an `ISecurityManager` mapping an authenticated caller to no principal let a
remote call reach the coordinator without one and skip every grant. Under no supported
configuration does a call through the `Map…Endpoints` now reach a facade without a principal: an
endpoint filter on every API group — which runs whatever the policies admit, and even under
`.AllowAnonymous()` — refuses an unauthenticated caller (challenge, 401) and an authenticated one
the host's `ISecurityManager` maps to no principal (403: the credentials are valid, so
authenticating again cannot help). Named host policies are also added to an always-required
authenticated user (D28). The filter makes no grant decision.

**Warning — other transports.** The coordinator cannot tell a call that crossed a transport from
an in-process one; it sees only whether there is a principal. Any transport a host binds the
coordinator to other than `MapExecutionCoordinatorEndpoints` — its own controller, a gRPC service, a
message consumer — must establish the caller's principal before calling it, or every coordinator
grant is skipped for its callers. The warning is repeated on `IExecutionCoordinator`.

**Amended (#35, D29):** a refused (`NotAuthorized`) renewal or completion is retried within the lease,
then the claim is lost or the outcome dropped (a dropped success is remembered and replayed); a
refused claim slows the worker's polling. The worker never pauses itself. "`SchedulerWorker` already logs and
swallows a failed completion report" above is history.
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

## D20 — Worker groups, host-wide numbering, per-worker management (execution rework, sub-phase C)

A worker host runs one or more **worker groups**. `AddSchedulingWorkers( WorkerOptions options =
null, Func<IServiceProvider, IExecutionCoordinator> coordinator = null )` replaces
`AddSchedulingWorker` and is called once per group. A group is `WorkerOptions.Workers` (default 1)
identical `SchedulerWorker`s sharing a `Claimant` and an optional `JobType` filter (null claims any
type); each worker claims, executes and reports one *attended* job at a time (amended in #10: D25
adds detached handlers, `WorkerInfo.DetachedJobs`, and the `CancellationGrace` and
`MaxDetachedHandlers` options).

- **Numbers are host-wide and deterministic**: 1..N across every group, in registration order,
  assigned at registration by an accumulator kept as a singleton instance in the service
  collection (the `JobTypeRegistrations` pattern). Unique-per-host numbers keep holders distinct
  even when two groups share a claimant; deterministic ones make a restarted host present the same
  holders again. A worker is constructed with its number; it claims with `ClaimDueJobAsync(
  claimant, number, JobType )` and renews and reports with the same number.
- **The default claimant is `Environment.MachineName`**, stable across restarts (the random suffix
  is gone). **Restart takeover** follows from D16: a restarted worker presenting the same claimant
  and number is the same lease holder (`claimant/number`), re-takes its own live lease and adopts
  its unfinished run instead of waiting out the lease. (#10, D23: the due pre-filter and
  `OccurrenceState.Classify` match `holder = @holder` on the schedule row.) The corollary: **two worker processes on one
  machine must set distinct claimants**, or their identically numbered workers would take each
  other's claims as their own.
- **Validation** (`WorkerOptions.Validate()`, run at registration and by the worker): claimant not
  blank and free of `/`; `Workers >= 1`; `JobType` null or not blank (#10 adds positive intervals,
  `CancellationGrace >= 0`, durations within `TimerBounds.Maximum` and `MaxDetachedHandlers >= 1`). The worker additionally
  requires a number of 1 or more. An invalid group fails at `AddSchedulingWorkers` and consumes no
  numbers.
- **Per-group coordinator credentials.** The optional `coordinator` factory supplies the group's
  `IExecutionCoordinator` — typically an `ExecutionCoordinatorClient` whose `HttpApiClient` carries
  the group's own credentials decorator, matching grants qualified by that group's claimant (D16).
  It is invoked once per group and the result shared by the group's workers; without it the group
  resolves `IExecutionCoordinator` from the container.
- **Per-worker management.** `ISchedulerWorker` stays per instance; the single `ISchedulerWorker`
  registration is replaced by `ISchedulerWorkerRegistry { Workers; Find( worker ) }` in
  Abstractions (implemented by `SchedulerWorkerRegistry` in `Pug.Scheduling.Worker`), so the
  multi-targeted endpoints can see it. `WorkerInfo` gains `Worker` and `JobType`. Routes and
  authorization: D15. Grants remain `SCHEDULER_WORKER` qualified by the worker's claimant, so a
  group's workers share them.
- **Graceful shutdown releases the claim** when the handler has stopped (D27); the worker's
  `AbandonTimeout` (default 5 s) bounds that call.
- **Each worker is its own `IHostedService` singleton**, registered as `AddSingleton<IHostedService>(
  provider => registry.Find( number ) )` — the same instance the registry hands to management.
  **Not `AddHostedService( factory )`**: it uses `TryAddEnumerable`, which deduplicates factory
  registrations by implementation type, so every `SchedulerWorker` after the first would be dropped
  silently. Tests assert N distinct hosted services and N workers polling; swapping
  `AddHostedService` back in fails them.
- Worker log lines that describe the worker (started, stopping, paused, resumed, claim failure,
  no due work) carry `{Claimant}/{Worker}` and the group's `{JobType}` (`any` when unfiltered); job
  lines keep the job's `{JobType}`.
- **Amended (#35, D29):** `WorkerInfo.Fault` (`WorkerFaults.ClaimRefused`) says the coordinator is
  currently refusing the worker's claims; it does not imply `Paused` and clears when a claim is
  answered. `WorkerOptions.RefusedPollRetryInterval` (default 1 min) paces a refused worker's polls.
  Restart takeover is unchanged; it is the reason the worker must not poll while it still holds a
  claim.

## D21 — Lease arbitration runs READ COMMITTED (defect found by the Postgres tests) — SUPERSEDED by D23

Historical: the lease provider is gone (D23). The READ COMMITTED finding survives as `UnitsOfWork.Write` (D24).

`Pug.Application.Data`'s `ExecuteAsync`/`PerformAsync` open a default `TransactionScope`, whose
isolation is **Serializable**, and Npgsql enlists at `SERIALIZABLE`. There the losing side of a
lease race does not see its conditional upsert affect no rows: Postgres fails it with `40001:
could not serialize access due to concurrent update`, so a coordinator that lost a claim race threw
instead of moving to the next candidate. SQLite serializes writers and never showed it.
`DatabaseExecutionLeaseProvider` now runs every operation in its own (`RequiresNew`) READ COMMITTED
transaction — the semantics the single-statement arbitration (D6) was written for — and lease
state is never committed as part of a caller's transaction.

The schedule store followed in D22.

## D22 — The schedule store runs READ COMMITTED; adoption is conditional (#5) — PARTLY SUPERSEDED by D24

Historical, with these parts no longer true after #10: "SQLite units are not atomic" (D24: they are,
through self-enlistment); the finished-occurrence guard and the conditional adoption
(`ClaimRunAsync`) were replaced by locking the row and deciding on it (D23); the single-statement
conditions and the "`UpdateDefinition` vs. completion" lost update are gone for the same reason
(#8, F3). What stands: units run in their own `RequiresNew` READ COMMITTED transactions
(`UnitsOfWork.Write`).

**Every schedule-store unit of `ExecutionCoordinator` and `Scheduler` runs in its own
(`RequiresNew`) READ COMMITTED transaction**, the D21 pattern. This was checked unit by unit: none
acts on a read made in the same transaction. The coordinator's reads (due candidates, latest run,
run, schedule) and the `Scheduler`'s read of the existing schedule are units of their own. The
multi-statement units only write: insert run + clear request, skip run + record occurrence,
complete run + record occurrence, and the `Scheduler`'s two deletes. Races between writers are
settled by single-statement conditions (`CompleteRunAsync`, now `ClaimRunAsync`) and by the
execution lease. Serializable therefore protected nothing. On Postgres it made the loser of a race
on one row fail with `40001` instead of having its conditional write affect no rows, or instead of
applying its write once the other committed. The race tests reproduce this when either facade is
put back to Serializable:
- a late completion racing an adoption: `40001` on either side;
- `Pause`/`RunNow`/`UpdateDefinition` racing a completion: `40001` on the management side, or a
  completion rolled back. A rolled-back completion leaves its run unfinished, to be adopted and
  executed again.

**Adoption is conditional.** `IDataSession.ClaimRunAsync` returns `bool` and updates only `where
identifier = @run and finished is null`. A late completion by the previous holder can land between
the claim's latest-run read and its adoption. When it does, the claim adopts nothing: it logs a
warning, releases the lease, leaves any run request alone, and moves to the next candidate. The
unconditional transfer handed the adopter an already-finished run to execute again.

**A claim never re-records a finished occurrence.** The due candidate is read before the lease is
taken, and the "advanced since the due read" check used that same stale read, so it could not fire.
A completion that landed between the due read and the latest-run read left the claim inserting the
same `(schedule, scheduledFor, attempt)` again. That fails on `uxScheduleRunFire`, a raw exception
out of `ClaimDueJobAsync`. Both providers showed it. When the latest run is finished and is
exactly the occurrence and attempt about to be recorded, the candidate is now discarded as stale,
the same way. The decision rests on the finished run itself, not on the schedule's advance:
**SQLite units are not atomic.** Microsoft.Data.Sqlite connections do not enlist in the
`TransactionScope`, so each statement commits on its own (a probe confirmed it: a write followed
by an exception is not rolled back, under either scope). A completion's finished run can therefore
be visible on SQLite before its schedule advance. On SQLite the isolation options are a no-op.

Trade-offs of leaving Serializable:
- **Pause / resume vs. completion:** different columns; both apply. No loss.
- **`RunNow` vs. completion:** last writer wins on `nextRun`/`runRequested`. A request recorded
  before the completion is cleared by it, as it always was for a request raised while a run is
  executing.
- **`UpdateDefinition` vs. completion: a lost update that predates D22.** With an unchanged
  recurrence, the update writes back the `nextRun` it read before the completion. If the
  completion commits in between, the schedule's `nextRun` reverts to the occurrence just executed.
  Nothing executes twice: every claim finds that occurrence and attempt already finished and
  discards the candidate (before D22 it failed on `uxScheduleRunFire` instead). But the schedule
  stalls until a later definition update or run request moves `nextRun`. When the recurrence
  changes, the completion's `nextRun`, computed from the old recurrence, can win once instead.
  Both reads sit in units separate from their writes, so Serializable never covered them; it only
  turned the in-flight subcase into `40001`. A variant of the race test that asserts the advance
  showed the lost update at Serializable as well as at READ COMMITTED, on both providers. Not
  fixed here. The fix is to write `nextRun` only when the recurrence changes, or only if it still
  holds the value read.
- **Ambient transactions:** like the lease provider, the facades no longer enlist in a caller's
  `TransactionScope`. A host that wrapped `RegisterAsync` in a transaction of its own no longer has
  the registration enlisted in it (Postgres; SQLite never enlisted).

## D23 — Execution state on the schedule row; every transition is lock, decide, write (#10; supersedes D6 and D21)

The coordinator's bugs (#7, #8, #9, F1, F3, the PR #6 patches) shared two causes. A running schedule
looked like an occurrence that had not started, so every claim inferred its state from the latest run
row and a separate lease. And no deciding read shared a transaction with its write. The redesign
(user decision, 2026-11-14) fixes both.

- **Schema v3 (nullable columns, v2 script names unchanged):** `activeRun`, `holder`
  (`claimant/worker`), `leaseExpiry` (unindexed, so heartbeats stay HOT updates on Postgres) and
  `occurrence` (the retry identity, non-null only while `attempt > 0`). The due indexes are recreated
  as partial indexes on `activeRun is null`, plus `ixScheduleRequested` and `ixScheduleActive`. The
  upgrade backfills `occurrence` from the latest run with `attempt > 0`, and turns each unfinished v2
  run into `activeRun`, with `holder` from its `claimedBy` and `leaseExpiry` at the epoch: lapsed, so
  the next claim adopts it.
- **Every transition is one write unit** (`UnitsOfWork.Write`): lock the schedule row, decide in C#
  on what was locked, write by identifier, commit. Claim, adopt, skip, renew and complete are
  transitions of `ExecutionCoordinator`; run-now, definition update and delete are `Scheduler`'s. The
  lock replaces every policy-bearing SQL condition (the guard, conditional adoption, the stale
  check).
- **Rules are small internal pure functions in `Pug.Scheduling`**, unit-tested without a database:
  `OccurrenceState.Classify` (adopt > MANUAL > RETRY > MISFIRE/SKIP/SCHEDULE),
  `CompletionRules` (retry, manual-run rules, baselines) and `DefinitionRules` (when `nextRun` may
  change). **`IDataSession` holds storage primitives only**: reads (`GetScheduleAsync`/
  `GetScheduleByActiveRunAsync( …, RowLock )`, `GetDueCandidatesAsync`, runs) and unconditional
  writes by identifier (`SetExecutionAsync`, `SetOccurrenceStateAsync`, …). No session method
  encodes a rule. The due query is the one place where SQL mirrors policy: a coarse pre-filter for
  index use, documented as mirroring `Classify`, returning a superset; the decision is re-made on
  the locked row.
- **The holder on the schedule row is the authority** for renewal and completion; a completion by a
  holder whose lease lapsed but was not adopted is accepted with a warning. `scheduleRun.claimedBy`
  is audit only. A schedule with a lease always has a run in progress.
- **Leases projects removed:** the four `Pug.Scheduling.Leases*` projects and
  `IExecutionLeaseProvider` are deleted; `ScheduleInfo.ExecutionLease` is composed from the row.
- **D20 restart takeover is by holder:** a claim whose holder equals the row's holder adopts the
  unfinished run even while the lease is live.
- **Delete locks the row first**, then removes runs and the schedule in one unit; a worker mid-run
  fails its next renewal and its completion gets `UnknownRunException` (unchanged behaviour).
- **Failing candidates are isolated:** a candidate that fails is logged at Error and the claim moves
  on (#7), but when every candidate failed (none claimed, none handled as "no longer qualifies"
  or passed over), the claim rethrows the last failure rather than reporting an idle window.
- **Claim lock mode (amended in #14):** the claim's per-candidate lock is `RowLock.SkipLocked`
  (Postgres `for update skip locked`); every other locking read (renew, abandon, complete, run-now,
  pause, definition update, delete) is `RowLock.Wait`. Concurrent claims read the same window in
  the same order, so waiting queued every coordinator behind the first candidate's holder, usually
  only to find it claimed. A skipped row reads as null, which the claim already treated as "next
  candidate" (logged at Debug as held by another unit or deleted); it is handled, not failed, for
  the rethrow above. Skipping is safe for the claim alone: it decides nothing on the skipped row,
  and a row still due after its holder commits is claimed at a later poll; when every candidate of
  the window is held, the claim reports no work and the worker polls again after `PollInterval`.
  Anywhere else a skipped row would read as "not held" or "missing": a renewal would tell its
  worker to abandon a run it owns, a completion would be refused, a management write would report
  the schedule unknown. Measured on Postgres 17 (Testcontainers, 200 due schedules, runs left
  active so the claim is the only locking read, each coordinator claiming back to back, median of
  3): no gain at 1–4 coordinators; at 8, 312 → 480 claims/s; at 16, 215 → 315 claims/s, p95 claim
  latency 66 → 39 ms; the share of busy backends waiting on a lock fell from 47–55 % to 0.
- **Clocks (amended in #12):** `ExecutionCoordinator` and `Scheduler` take an `ISchedulingClock`
  (`Pug.Scheduling`), not a `TimeProvider`. Every coordination decision and every time they write —
  due-ness (the candidate read included), misfire classification, lease expiry on claim, adoption,
  renewal and abandonment, the completion lapse check, retry and next-occurrence times, run
  `Started`/`Finished`, registration `NextRun` and the audit `ActionContext` stamps — reads "now"
  through `GetUtcNowAsync( session )` inside its unit, after the row lock where there is one, so a
  lock wait never leaves it stale. Run-now, pause and delete decide nothing by time.
  - **`DatabaseClock` is the default** (`AddScheduling` registers it with `TryAdd`): it calls the
    storage primitive `IDataSession.GetCurrentTimeAsync`, which decides nothing — Postgres
    `clock_timestamp()` (the actual time; `now()` is the transaction's start, which a unit that
    waited on a lock has passed), SQLite `strftime( '%Y-%m-%d %H:%M:%f', 'now' )` (milliseconds,
    stamped UTC per D18). All coordinator hosts then judge leases by one clock, so host clock skew
    can no longer let a host whose clock runs ahead adopt a live run. SQLite's clock is its host's
    own: a SQLite store is single-host in practice. A due-candidate read served by a standby uses
    the standby's clock and its replication lag; that is safe only because every candidate is
    re-judged on the primary, at the primary's time, under its row lock. A forward step of the
    database clock shortens every live lease by the step, so `LeaseDuration` must comfortably exceed
    `HeartbeatInterval` plus any such step.
  - **`TimeProviderClock( TimeProvider )`** is for tests (with `ManualTimeProvider`) and single-host
    deployments. A host registers it as `ISchedulingClock`; a registered `TimeProvider` alone does
    not change the facades' clock. Several hosts on their own clocks are skew-sensitive again:
    `LeaseDuration` must then exceed their skew.
  - The worker keeps its `TimeProvider`: it measures only local durations and stamps
    `WorkerInfo.LastPoll`, which is informational. (#35, D29: its lease deadline is measured on the
    `Stopwatch` from `ClaimedJob.ClaimDuration`, never compared with the coordinator's clock.)
- Run-now refusal, manual-run rules and the occurrence rules: D10, D11, D12.

## D24 — SQLite self-enlistment, UnitsOfWork, row locks, shared session base (#10; partly supersedes D22)

Microsoft.Data.Sqlite ignores `TransactionScope`, so on SQLite every statement autocommitted (the
`docs/KNOWN-ISSUES.md` entry, #9). Postgres (Npgsql) enlists natively.

- **SQLite sessions enlist themselves** (the Pug.Sal pattern, Sal D15/D19): when a scope is active
  `DapperData` opens a real SQLite transaction on the session and enlists it through
  `EnlistVolatile`. A write unit begins `IMMEDIATE` (the write lock up front, which serializes
  writers: no row lock is needed), a read unit begins deferred. The commit is in phase one
  (`Prepare`), so a failed commit aborts the scope; the session's `Dispose` waits for the
  enlistment. Postgres does not copy it.
- **`UnitsOfWork` (Data.Abstractions):** `Write` is `RequiresNew` + ReadCommitted, `Read` is
  `RequiresNew` + RepeatableRead (a snapshot; a read-only transaction never fails at that level).
  Nothing in the domain layer begins or commits a transaction, and no unit joins a caller's
  `TransactionScope`.
- **Postgres row locks:** a write unit that acts on what it read reads the row with `for update`
  (`RowLock.Wait`; the dialect override `LockingReadClause( RowLock )`). The loser of a race waits
  and then decides on the committed row, instead of failing with `40001` under Serializable (D21,
  D22). The claim alone uses `RowLock.SkipLocked`, `for update skip locked` (D23, #14). SQLite
  maps every mode to no clause: its `IMMEDIATE` write unit holds the database write lock, so there
  is no row contention to wait on or skip, and a claim there waits for a peer's whole unit.
- **Shared `DapperDataSessionBase`** (new `Pug.Scheduling.Data.Dapper`): the SQL, row types,
  `Compose` and the JSON handler are written once. Providers override only the dialect:
  enabled/paused predicate, run-requested predicate, locking clause, current-time expression (#12,
  D23), and SQLite's D18 UTC stamping (`ReadTimestamp`). This replaces the hand-mirrored Postgres session. The due-candidate read is a
  `union` of three bounded branches, one per partial index, because an `or` is a full scan on
  SQLite.
- **Host requirements on SQLite:** WAL journal mode is what makes "reads block nobody" true (a
  deferred read snapshot does not block the writer); without it readers and the single writer
  contend. DbUp must apply `GetSchemaUpgradeScripts()` in group order (schema version, then
  sequence): DbUp sorts by script name, so the tests pass `RunGroupOrder`.
- **D22 corrections:** "SQLite units are not atomic" is false after #10, and the guard and conditional
  adoption are replaced by D23.
- Follow-up: the enlistment now exists in Pug.Sal and here; its home is a shared package (#13).

## D25 — Handlers that ignore cancellation are detached (#10, sub-phase D)

The worker used to await the handler directly, so a handler ignoring cancellation held the worker
past its timeout, past claim loss, and past shutdown.

- The handler runs off the pump; the worker waits for it to finish or for a stop signal (timeout,
  claim lost, host stopping), then cancels it and waits up to `WorkerOptions.CancellationGrace`
  (default 10 s, below the host's 30 s shutdown timeout; zero detaches at once).
- A handler still running after the grace is **detached**: the worker reports TIMED_OUT, or abandons
  without a report on claim loss or shutdown, stops the heartbeat and moves on. The handler and its
  DI scope are disposed when its task ends, exactly once. `WorkerInfo.DetachedJobs` counts them
  (`ActiveJobs` counts attended jobs only).
- **`MaxDetachedHandlers`** (default 2, at least 1): at that many the worker stops claiming and logs
  an Error once, until one ends.
- The invariant is "one *attended* job at a time". **A detached handler may overlap its own retry**
  (D7); `IScheduledJobHandler` documents that handlers must honour cancellation.
- **Guarded cancellation:** timeout, claim loss and shutdown cancel through one guarded `Cancel`, so
  a throwing third-party cancellation callback cannot fault the heartbeat, the pump or the process.
- **`TimerBounds.Maximum`** (2^32-2 ms, about 49.7 days; Abstractions) is the longest duration a
  timer accepts. It is validated on both ends: `Scheduler` rejects a longer `Timeout` (and
  `SchedulerOptions.DefaultTimeout`) with `ArgumentException` (400), and the worker fails a run whose
  claimed timeout is out of range (a remote coordinator is not trusted) rather than letting it stop
  the host. `WorkerOptions` durations are validated likewise (D20).
- **Amended (#35, D29):** the same guard fails a run whose `ClaimDuration` is below
  `SchedulerWorker.MinimumClaimDuration` (1 s) or exceeds `TimerBounds.Maximum`; and a renewal still
  pending is awaited only until the lease deadline, so a hung coordinator no longer delays the detach.

## D26 — Registering on behalf requires CREATE_ON_BEHALF on the owner principal (#16)

The on-behalf check used to be `CREATE` on the schedule object type in the domain, so anyone allowed
to create schedules could create them for **any** principal.

- Registering for an owner who is not the current USER checks **`CREATE_ON_BEHALF`** against a
  `NounQualifier` of `Type = owner.Type`, `Identifier = owner.Identifier`, `Domain =
  definition.Domain` (`ResourceQualifiers.Principal`). The caller needs an explicit grant to act
  for that principal, in that domain.
- **`CREATE` is removed**, not kept alongside: it was used only there. Self-registration still
  needs no grant (owner rule).
- Owner types map directly onto security object types; no mapping table.

## D27 — Graceful shutdown releases the claim (#11)

On host shutdown the worker abandoned its run without telling the coordinator, so the claim stayed
held until the lease lapsed (up to `LeaseDuration`) before a peer could adopt the run.

- **`IExecutionCoordinator.AbandonClaimAsync( run, claimant, worker )`** returns true when the caller
  held the claim and it was released; false when it is not held (adopted by a peer, run finished,
  unknown run, schedule deleted). It never throws `ClaimNotHeldException`: abandoning is best effort.
- **One locked write, same shape as renewal** (D23): lock the schedule by active run, check the
  holder, write the execution with `Expiry = now`. The run, active-run and holder are untouched, so
  the next claim by any worker classifies the run as an adoption (D9) and takes over the same run row
  with its trigger kept. Renewal and abandonment share one helper (`SetHeldLeaseExpiryAsync`); the
  data session stays free of decisions.
- **Completion after abandon.** The abandoned lease is merely lapsed, so the holder's own late
  completion is accepted until a peer adopts the run (the lapsed-but-holder-matches rule, D16/D23),
  and refused with `ClaimNotHeldException` afterwards. Abandoning is therefore safe to attempt even if
  the handler's result is still racing in.
- **New grant `ABANDON_CLAIM`** (`CoordinatorSecurityOperations.AbandonClaim`) on
  `SecurityObjectTypes.ExecutionCoordinator`, qualified by the claimant like the others; skipped for
  an in-process caller with no principal. A remote worker host must be granted it, but a host
  without it merely keeps today's behaviour (the call is refused, logged at Warning, and the lease
  lapses).
- **Transport:** `POST executionCoordinator/JobRuns/{run}/abandonment?claimant=&worker=` answers 200
  with a bool, plus 400 and 403 as for renewal; `ExecutionCoordinatorClient.AbandonClaimAsync`.
- **The worker releases only when the host is stopping and the handler has stopped** (finished, or
  honoured cancellation within the grace) and the claim was not lost. A **detached** handler is still
  running, so releasing would let a peer adopt the run while it executes; its lease is left to lapse
  (D25's overlap is then bounded by the lease, as before). A lost claim holds nothing to release;
  timeouts already report TIMED_OUT, which releases the claim.
- **Best effort, bounded:** the call is limited by `WorkerOptions.AbandonTimeout` (default 5 s,
  positive, within `TimerBounds.Maximum`), logged at Warning on failure, and never throws out of the
  pump. It adds at most that bound to a stopping worker, so keep `CancellationGrace + AbandonTimeout`
  below the host's shutdown timeout.
- **Amended (#35, D29):** the shutdown budget must also allow for a completion report in flight. A
  refusal releases nothing.

## D28 — Host-configurable base path and authorization policies per API (#28, #29; amends D15)

D15's first route segments (`schedules`, `executionCoordinator`, `workers`) are now defaults, and a
host may also gate each API with its own authorization policies.

- **One options class per API**, in its WebApi package: `SchedulingEndpointOptions`,
  `ExecutionCoordinatorEndpointOptions` (`Pug.Scheduling.WebApi`) and `SchedulerWorkerEndpointOptions`
  (`Pug.Scheduling.Worker.WebApi`), each with `BasePath` and `AuthorizationPolicies`. Passed to the
  `Map…Endpoints( options? )` call; omitted, today's behaviour. Invalid values throw
  `ArgumentException` when the API is mapped.
- **Defaults live once** in `Pug.Scheduling.WebApi.Models` (`ApiBasePaths`), with the one
  normalization (`Normalize`: trim `/`, non-empty) that server options and clients share, so a client
  and a server given the same base path agree.
- **Each Map method makes a `RouteGroupBuilder`** on the base path, applies the authorization once
  on the group and maps endpoints relative to it; it **returns the group** so the host can add
  conventions. The per-endpoint `RequireAuthorization()` calls are gone.
- **Amended (#34): an authenticated caller with a principal is required whatever the policies.**
  With no `AuthorizationPolicies` the group applies the host's `DefaultPolicy`, as before:
  displacing it would silently drop a stricter one (a required scope claim) and break one bound to a
  non-default authentication scheme. With names, the group requires an inline authenticated-user
  policy (`RequireAuthenticatedUser`) and the named policies, all of which must pass, so a
  permissive named policy cannot admit an anonymous caller; as with any policy object, the
  `DefaultPolicy` is then not applied. Either way the group carries an endpoint filter refusing a
  caller without authentication (401) or without a principal from `ISecurityManager` (403), whatever
  the policies admit — a permissive `DefaultPolicy` included — and even under `.AllowAnonymous()`.
  The filter resolves `ISecurityManager`, so a host mapping these APIs must register one; without
  it every request is a 500. Both live once
  in `Pug.Scheduling.WebApi`'s internal `ApiAccess`, compiled into `Pug.Scheduling.Worker.WebApi` as a
  linked source file, so the worker host still references no server package of the scheduler (D15).
  This is what D16's trust in a missing principal rests on.
- **The policy is a transport gate, not a grant decision.** It narrows who may call the API at all;
  the facades (`Scheduler`, `ExecutionCoordinator`) and, for worker management, the endpoint handlers
  still enforce the ACL grants (D16, D20), so an in-process caller is held to the same rules.
- **Clients** take an optional `basePath` (default from `ApiBasePaths`), normalized and validated alike.
- **`Pug.Scheduling.WebApi` and `Pug.Scheduling.Worker.WebApi` drop net6.0** (net8.0;net10.0) for
  `MapGroup`; the `NET7_0_OR_GREATER` branches are gone. `Worker.WebApi` now references
  `WebApi.Models` (no ASP.NET dependency) for the defaults.

## D29 — The worker's claim lifecycle (#35)

The worker had no model of a claim's life: it treated "I stopped attending" as "I no longer hold it".
Every coordinator failure was logged and carried on. A refused renewal (403) kept the handler running
past its lease, so a peer adopted and ran it concurrently. And after an unreported run the schedule
row still named the worker as holder, so its next poll — at once — adopted the same run (D20), and a
refused or failing completion re-executed it in a tight loop that never used up an attempt.

- **Failure classification**, documented on `IExecutionCoordinator` and applied in the worker's catch
  clauses (no shared helper: the worker is its only consumer):
  - *refused*: `NotAuthorized` (403). It concerns the caller, not the run: a missing grant, or a
    security provider failing closed while its backend is down — the two read the same;
  - *not held*: `false`, `ClaimNotHeldException`, `UnknownRunException`;
  - *unconfirmed*: everything else — 401 `AuthenticationException` (a token may refresh), 5xx,
    network, timeouts, and `ArgumentException`/`InvalidOperationException` (one schedule's bad stored
    data must not pause every worker).
  The Worker project references `Pug.Application.Security.Abstractions` for `NotAuthorized`.
- **The worker knows its lease.** `ClaimedJob.ClaimDuration` is the coordinator's `LeaseDuration`, on
  claim and on adoption. The coordinator requires `LeaseDuration` of at least
  `ExecutionCoordinatorOptions.MinimumLeaseDuration` (10 s, user decision) and at most
  `TimerBounds.Maximum`. The worker's deadline is a `Stopwatch` timestamp taken *before* the
  request + its *window*, `ClaimDuration` − `SchedulerWorker.LeaseTolerance` (500 ms), advanced only
  by a renewal that answers true (from that renewal's own pre-request timestamp). Skew and round trip
  can only move it earlier than the coordinator's. A renewal restarts the full `ClaimDuration`, so
  **every coordinator instance on one store must share one `LeaseDuration`**.
- **The worker's own guard.** A `ClaimDuration` below `SchedulerWorker.MinimumClaimDuration` (twice the
  tolerance, 1 s) or beyond `TimerBounds.Maximum` fails the run unexecuted (D25's guard): a remote
  coordinator of another version is not trusted.
- **Upgrade order: the scheduler (coordinator) hosts before the worker hosts.** An older coordinator
  sends no `ClaimDuration`, which reads as zero, and a new worker fails every run it claims unexecuted.
- **Renewal.** `beat = Min( HeartbeatInterval, window / 3 )` (user decision): even a `HeartbeatInterval`
  at or above the lease leaves a renewal and a genuine retry within each window. The claim is lost
  (`LoseClaim( NotHeld | Lapsed )`, handler cancelled, nothing reported) on false, or at the deadline. When the remaining window is no longer than a beat, the worker waits
  the whole remainder and lapses after it without re-reading the deadline, and a renewal still
  pending when its wait (the remaining window) times out is the lapse itself. Timers work in whole
  milliseconds and may fire a little before the `Stopwatch` deadline; re-reading a tiny-positive
  remainder made the heartbeat spin renewals at the deadline (found in review, 4/20 runs), each of
  which could still land and extend the lease of a run already given up. Other failures, refusals
  included, are logged and retried at the next beat.
- **Completion.** Recorded → done. Not held → Warning, outcome dropped. Anything else, a refusal
  included → retried every `PollInterval` until the lease deadline: past it a peer may adopt the run
  and refuse the report (until then a lapsed holder's report is still accepted, D23). `TIMED_OUT` reports get the same window. (Round
  2 also ended the window at the run's timeout, "since past it the job counts as failed"; that premise
  was false — the coordinator never compares a run with its `Timeout`, it only copies it into
  `ClaimedJob` — so a job finishing near its timeout lost its retries, and was removed in round 3.)
  The last wait is the whole remainder followed by one final attempt (the heartbeat's shape); the
  first attempt is made even past the deadline (a lapsed holder's report is accepted until a peer
  adopts, D23). The report of a claim the worker could not time gets that one attempt only. Then
  Error (naming `REPORT_COMPLETION` when refused) and dropped; nothing is released.
- **Unrecorded successes are replayed, not re-run** (user decision, round 3). A dropped report means
  the run is re-adopted and executed again at the same attempt; for a success that repeats side
  effects for nothing. So the worker remembers every SUCCEEDED outcome it could not get recorded —
  dropped at the lease deadline, answered "not held", or completed after its claim was lost — in a
  `UnrecordedSuccesses` store keyed by occurrence `(Schedule, ScheduledFor)` (the domain's idempotency
  key; normalized to UTC whole microseconds, since Postgres stores microseconds while a first claim
  may carry 100 ns ticks). When the occurrence is claimed again, under any run, the worker skips the
  handler (and the heartbeat) and reports the remembered outcome through the usual `ReportAsync`,
  under the new claim and lease. The entry is removed only once recorded: on "not held" a peer owns
  the run, but if it fails, its retry is the same occurrence and may come back here. An entry whose
  `JobType` or `Payload` changed since is dropped and the job runs (Warning). Failures and
  `TIMED_OUT` are never remembered (running them again is what a retry is for), nor is a detached
  handler's late result. A replay re-sends the original outcome, `Details` included: it is the true
  result of the work done. Remember and forget happen in one place, after the report; a success
  reached after the claim was lost comes back from classification marked `ClaimLost` (never
  reported) and goes through the same block.
  - *Bounds:* `Retention` 24 h from the first remember (on the `TimeProvider`'s timestamp) and
    `Capacity` 1,000, oldest evicted first, both enforced on every call. An occurrence comes back only
    while its run is active; past the bounds, handler idempotency is the backstop, as it always is.
  - *Scope and limits:* in memory, per host, per coordinator instance. `AddSchedulingWorkers` gives
    every group on one coordinator instance (compared by reference) one shared store, so any worker
    of the host replays; a group with its own coordinator factory gets its own, since another
    scheduler's schedule identifiers could collide. A restart, or adoption by another host,
    re-executes: handlers must stay idempotent on `(Schedule, ScheduledFor)`.
- **A single report attempt has no worker-side limit** (user decision): the coordinator's transport and
  store must keep finite timeouts (HttpClient 100 s by default, Npgsql's command timeout 30 s, SQLite's
  busy timeout), and a host's shutdown budget must allow for one attempt in flight. Documented on
  `IExecutionCoordinator`.
- **A host stop during report retries drops the outcome without releasing the claim**: the coordinator
  was not answering, so a release would most likely fail too, and the shutdown would wait out
  `AbandonTimeout` for nothing; the claim lapses with its lease.
- **The pump waits after an unrecorded run** (user decision). After a run whose report was not
  recorded — abandoned, not held, dropped (refused or unconfirmed), or failed for an unusable claim — the
  pump waits `PollInterval` before claiming again, cut short by a state change or the host stopping,
  as after a poll that found nothing. The row may still name this worker as holder, and an immediate
  claim would adopt the same run (D20). Together with the retries above, a run whose reports do not
  get through is claimed again at most once per lease plus `PollInterval` (the pump does not claim
  while retrying), or once per `PollInterval` when a report fails at once (an unusable claim, a proxy
  404 read as an unknown run). A success is then replayed, not executed again; a failure or timeout
  is executed again. After a recorded report the pump polls again at once.
- **Refusals are retried; the worker never pauses itself** (user decision, revising a first design in
  which a refused renewal or completion released the claim and paused the worker with a fault until
  an operator resumed it). The coordinator's check is `ISecurityManager.CurrentPrincipal
  .IsAuthorizedAsync(…)`, and a fail-closed security provider answers "not authorized" while its
  backend is down: a blip would have parked every worker until each was resumed by hand. So:
  - *renewal*: a refusal is unconfirmed — a Warning naming `RENEW_CLAIM`, retried at the beat; at the
    deadline the claim is lost as `Lapsed`, with an Error naming the grant;
  - *completion*: unconfirmed, within the window above; dropped with an Error naming
    `REPORT_COMPLETION`;
  - *claim*: nothing is held or executed, so the worker keeps polling, but at
    `WorkerOptions.RefusedPollRetryInterval` (default 1 min; positive, within `TimerBounds.Maximum`;
    no opt-out to stay idle — an operator who wants that uses `Pause()`) instead of `PollInterval`,
    sparing the coordinator and its security backend. Pause/resume or the host stopping cut the wait
    short. `WorkerInfo.Fault` is `WorkerFaults.ClaimRefused` from the first refusal until a claim is
    answered (with a job or none); one Error per episode, Information on recovery. `Fault` does not
    imply `Paused`, and `Resume()` does not clear it.
- **The cost of a grant that really is missing**, now that nothing stops the worker:
  - without `REPORT_COMPLETION`: each job runs once; its success is remembered, and every later claim
    of the occurrence on this host replays it (a failure is executed again, as any retry would be).
    The occurrence stays claimed — re-adopted once per lease plus `PollInterval` — until granted, and
    the stuck run holds back the schedule's later occurrences meanwhile; once it is recorded, misfire
    handling applies to what elapsed;
  - without `RENEW_CLAIM`: every run longer than the window (`ClaimDuration` − 0.5 s) is cut at the
    deadline and repeated, once per lease plus `PollInterval`, side effects repeated each time. Replay
    does not apply: the handler never finished.
  Operators must alert on the Errors above; the worker recovers by itself once granted.
- **`WorkerInfo.Fault` reflects the last claim that was answered or refused**, dated by `LastPoll`. It
  is not re-evaluated while the worker does not poll, so it may stay `CLAIM_REFUSED` while the worker
  is paused or at its detached limit.
- **Release rule.** An abandoned run whose handler stopped is released only when the host is stopping
  and the claim was not lost. A detached handler is never released (D27); a lapsed or not-held claim
  has nothing to release; a refusal releases nothing.
- **Coordinator adoption is unchanged.** The row cannot tell a restarted holder from a running one;
  only the worker knows, so the fix is the worker's (D9, D20 kept).
- **Client: every failure status throws.** Pug.HttpApiClient 0.6.0's `EnsureSuccessResponse` throws
  only for the statuses it lists and returns the rest (503, 504, 429, 408) as responses, so a 503 on
  completion read as recorded. `ApiFailures.EnsureSuccess` throws `HttpApiRequestException( response )`
  for every raw-response call of both clients (the typed helpers already check). Fixing the default
  upstream is a separate Pug.HttpApiClient issue.
- **Residual risks (accepted):**
  - the `Stopwatch` does not count a whole-system suspend (a VM paused): the worker then overestimates
    its lease until the next answered renewal;
  - a claim lost (lapsed) just as the handler completes is never reported: a success is remembered
    and replayed on this host, a failure is lost and the occurrence runs again;
  - the handler starts without checking whether the claim already lapsed during a slow claim round
    trip; the heartbeat then lapses it at once;
  - a renewal that blocks synchronously, before returning its task, is not bounded by `WaitAsync`.
- **Follow-up (not fixed here): poison completion.** A completion that throws for the schedule's own
  data (a stored zero interval, #33) is *unconfirmed*, so the run is re-executed once per lease,
  forever; the coordinator should complete the run and park the schedule, or cap deliveries per run.

## Verification conventions

Completion gate: `dotnet build Pug.Scheduling.slnx -warnaserror -m:1` clean **and**
`dotnet test tests/Pug.Scheduling.Tests` green. The store-backed suites (coordinator,
scheduler, worker groups, migration) are generic over fixture interfaces and run against both
providers: SQLite (per-fixture temp DB, DbUp-migrated from `GetSchemaUpgradeScripts()` in group order,
WAL + busy_timeout) and PostgreSQL via Testcontainers (one `postgres:17-alpine` container per test run,
a DbUp-migrated database per fixture). The Postgres classes carry `[Trait( "Category", "Postgres" )]`
and need Docker (on this machine: `sg docker -c "dotnet test tests/Pug.Scheduling.Tests"`); without
Docker run `--filter "Category!=Postgres"`. The HTTP/API suites stay on SQLite.

## Phase status (per approved plan; pause for user review after every sub-phase)

- **1a Models + Abstractions — DONE**  · **1b Data.Abstractions — DONE** · **1c Leases — DONE**
  · **1d implementation (core/data/worker/DI + 44 tests) — DONE**
  · **1e WebApi (schedule, execution coordinator and worker endpoints, 29 endpoint tests; 75
  total) — DONE**
  · **1f `.Client` (both facades), packaging metadata, README — DONE (88 tests)**
- **Execution rework** (plan: single-job workers, no dispatcher, worker groups):
  **A coordinator contract (`claimant` + `worker`, one job per claim, job-type filter, candidate
  window, schema v2 index) and coordinator logging — DONE (107 tests)**; **B job-handler projects /
  factory registry / no dispatcher (D19, 123 tests) — DONE**; **C worker groups, host-wide
  numbering, per-worker management (D20, 154 tests) — DONE**.
- **Postgres test coverage (#3)** — store-backed suites on both providers, Postgres via
  Testcontainers, plus a many-coordinators claim race; lease arbitration fixed to READ COMMITTED
  (D21) — **DONE (210 tests: 155 SQLite/other, 55 Postgres)**.
- **Schedule-store isolation (#5)** — coordinator and scheduler units READ COMMITTED, conditional
  adoption, finished-occurrence guard on claim; late-completion-vs-adoption and
  management-vs-completion race tests on both providers (D22) — **DONE (214 tests: 157
  SQLite/other, 57 Postgres)**.
- **Coordinator redesign (#10)** — lease on the schedule row, lock-decide-write transitions, atomic
  SQLite units, handler detach (D23, D24, D25). **A** shared Dapper session, `UnitsOfWork`, SQLite
  enlistment, `TimeProvider`; **B** schema v3, state machine, run-now refusal, Leases removed;
  **C** interval grid and per-policy baselines; **D** worker detach, `TimerBounds`; **E** documentation
  reconciliation — **DONE (310 tests)**, awaiting the user's single review (no pause between
  sub-phases, by user decision, for this work only). Closes #7, #8, #9. Follow-ups: **#11**
  (`AbandonClaimAsync`: graceful shutdown releases the lease; **DONE**, D27), **#12** (database-clock lease expiry: `ISchedulingClock`, `DatabaseClock` by default; **DONE**, D23),
  **#13** (shared SQLite enlistment package), **#14** (`for update skip locked` on Postgres claims;
  **implemented**, D23 amendment, awaiting review).
- **Worker claim lifecycle (#35)** — `ClaimedJob.ClaimDuration`, the worker's own lease deadline,
  failure classification, refusals retried (never a self-pause; `CLAIM_REFUSED` and
  `RefusedPollRetryInterval` for refused claims), a report window bounded by the lease alone,
  unrecorded successes replayed (`UnrecordedSuccesses`), and clients throwing on every failure status
  (D29) — **implemented, awaiting review**.
- **Phase 1 COMPLETE.** Carry-over: webhook dispatch (D14, superseded by D19: a webhook is now a
  handler) still deferred.
- **Phase 2** `Pug.ReportConfigurations` (sub-phases 2a–2e, same shape as this repo).
- **Phase 3** SaaS integration (handler + `IEmailSender`/MailKit + `IEmailSenderProvider`,
  recipient policy, output store, link-first delivery ≤ 5 MB attachments, owner impersonation at
  generation time — runs execute as the stored owner, never system).
- **Phase 4** hardening + Corea-style demo host.
