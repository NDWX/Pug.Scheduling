# Pug.Scheduling

Tenant-aware, database-backed job scheduling for the `Pug.*` family. Schedules carry their tenant
(`Domain`), owner principal, and an arbitrary job payload as first-class, queryable data; workers
claim due occurrences through a single facade and never touch a data store, wherever they are
hosted.

Design rationale for every major shape lives in [`docs/DECISIONS.md`](docs/DECISIONS.md).

## Packages

| Package | What it is for |
|---|---|
| `Pug.Scheduling.Models` | Entities and wire contracts. Referenced by everything. |
| `Pug.Scheduling.Abstractions` | `IScheduler`, `IExecutionCoordinator`, `ISchedulerWorker`, `ISchedulerWorkerRegistry`, security constants. |
| `Pug.Scheduling` | The facades: `Scheduler` (management) and `ExecutionCoordinator` (execution). |
| `Pug.Scheduling.Data.Abstractions` · `.Data.Dapper` · `.Data.Postgres` · `.Data.SQLite` | Schedule store contract (storage primitives and `UnitsOfWork`), the shared Dapper session, and the Postgres and SQLite providers. |
| `Pug.Scheduling.JobHandler.Abstractions` | `IScheduledJobHandler`, `IScheduledJobHandlerFactory` and its registry — all a handler author references. |
| `Pug.Scheduling.Worker` | `SchedulerWorker` — the execution pump, one attended job at a time — and `WorkerOptions`. |
| `Pug.Scheduling.Worker.DependencyInjection` | `AddSchedulingWorkers` (one call per worker group), `AddScheduledJobHandler<T>`, `AddScheduledJobHandlerFactory<T>` — the worker host's DI. |
| `Pug.Scheduling.DependencyInjection` | `AddScheduling`, job type registration (`AddSchedulableJobType`, `AddJobPayloadValidator`). |
| `Pug.Scheduling.WebApi.Models` | HTTP surface contracts shared by the endpoints and the clients. |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints()`, `MapExecutionCoordinatorEndpoints()`. |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints()` — separate so a worker host references nothing it does not need. |
| `Pug.Scheduling.Client` | `SchedulerClient` and `ExecutionCoordinatorClient` over the HTTP surfaces. |

## Concepts

A **schedule** is a recurrence (cron in an IANA time zone, or a fixed interval) plus a job type and
an opaque payload, owned by a principal. The scheduler computes the next occurrence; it never
executes anything.

A **worker** polls `IExecutionCoordinator` for due occurrences, hands each to an
`IScheduledJobHandler` created by the factory registered for its job type (disposed after the run),
and reports the outcome. Running a job elsewhere, such as calling a webhook, is the handler's business.
Registering a handler does not make a job type schedulable; hosts that accept schedules for it also
call `AddSchedulableJobType`. Due-ness, claim exclusivity,
misfire handling, retry scheduling, and next-occurrence computation all live behind the
coordinator — a worker sees none of it, and binds the same interface whether it runs in-process or
against a remote scheduler.

Exclusivity comes from the execution state on the schedule's own row (active run, holder, lease
expiry): every claim, renewal and completion is one transaction that locks the row, decides, and
writes. Leases are short (2 minutes by default) and heartbeat-renewed, so a crashed worker's
occurrence is adopted again once its lease lapses: execution is **at-least-once**, and handlers
should key side effects on `(Schedule, ScheduledFor)`. Due-ness, misfires and lease expiry are
judged by the database's clock, read inside each transition after its row lock, so clock skew
between scheduler hosts does not matter. A handler must honour
its cancellation token: one that does not is detached after `CancellationGrace` and may still be
running when its occurrence is retried. On a graceful shutdown a worker whose handler has stopped
releases its claim (`AbandonClaimAsync`, grant `ABANDON_CLAIM`, bounded by `WorkerOptions.AbandonTimeout`,
default 5 s), so a peer adopts the run at once instead of after the lease; a detached handler's
claim is left to lapse.

Run-now (`POST schedules/{identifier}/run`) only raises a request marker; it never moves the next
occurrence. It is refused with `RunInProgressException` (409) while a run of the schedule is in
progress, live or lapsed, and a manual run is not retried if it fails.

## Hosting the scheduler

The host registers the schedule store (the only store: execution state lives on its schedule rows,
and there is no separate lease provider), then the framework:

```csharp
services.AddSingleton<IApplicationData<IDataSession>>(
        new Pug.Scheduling.Data.Postgres.DapperData( scheduleConnectionString, NpgsqlFactory.Instance ) );

// ISecurityManager comes from the host's own security stack.

services.AddScheduling( identifierGenerator );

services.AddSchedulableJobType( "report" );                          // accepted at registration
services.AddScheduledJobHandler<ReportJobHandler>( "report" );      // executes here (Worker.DependencyInjection)
services.AddJobPayloadValidator<ReportPayloadValidator>( "report" ); // optional, checked at registration

services.AddSchedulingWorkers( new WorkerOptions { Workers = 4 } );   // omit where jobs execute elsewhere
// WorkerOptions also sets Claimant, JobType, PollInterval, HeartbeatInterval,
// CancellationGrace (default 10 s) and MaxDetachedHandlers (default 2).
```

The store exposes `GetSchemaUpgradeScripts()`; run them in the order given (schema version, then
sequence) with DbUp or your migration tool of choice before first use. DbUp sorts by script name, so
give each script group its own `RunGroupOrder` to keep that order.

**Clock.** `AddScheduling` registers `DatabaseClock` as `ISchedulingClock`: every time the facades
decide on or record (due-ness, lease expiry, run and audit timestamps) is the database's
(`clock_timestamp()` on Postgres; on SQLite, the host's own clock, so a SQLite store is single-host).
Register another `ISchedulingClock`, such as `new TimeProviderClock( timeProvider )`, for tests or
a single host. Several hosts on their own clocks judge each other's leases by them, so
`LeaseDuration` must then exceed their clock skew. A registered `TimeProvider` alone does not change
the facades' clock. On any clock, a forward step (for example an NTP correction on the database
server) shortens every live lease by the size of the step, so `LeaseDuration` must comfortably exceed
`HeartbeatInterval` plus any such step.

**SQLite host requirements.** Enable WAL journal mode (`PRAGMA journal_mode = WAL`) and a busy
timeout: units of work run as real transactions (a write begins `IMMEDIATE`), and under WAL readers
do not block the writer. Apply the schema scripts in group order, as above.

**Status codes.** The facades' exceptions map to the endpoints' responses, and the clients map them
back:

| Response | Exception | Raised by |
|---|---|---|
| 400 `BadRequestResultInfo` | `ArgumentException` | invalid definition, including a `Timeout` over `TimerBounds.Maximum` (about 49.7 days) |
| 403 | `NotAuthorized` | any operation without the grant |
| 404 | `UnknownScheduleException` / `UnknownRunException` | unknown schedule or run |
| 409 `RunInProgressResultInfo` | `RunInProgressException` | run-now while a run is active |
| 409 | `ClaimNotHeldException` | completion by a worker that does not hold the run |

Endpoints carry their own route prefixes, so map them at the root:

```csharp
app.MapSchedulingEndpoints();            // schedules/...
app.MapExecutionCoordinatorEndpoints();  // executionCoordinator/...   (only if workers run elsewhere)
app.MapSchedulerWorkerEndpoints();       // workers/...                (only if workers run here)
```

## Hosting a worker out-of-process

The worker host references `Pug.Scheduling.Worker.DependencyInjection` and `Pug.Scheduling.Client` — no data stores, no
server-side packages — and binds the coordinator to its HTTP client:

```csharp
services.AddSingleton<IExecutionCoordinator>(
        provider => new ExecutionCoordinatorClient(
                new HttpApiClient(
                        schedulerBaseUrl,
                        provider.GetRequiredService<IHttpClientFactory>(),
                        messageDecorators: new[] { credentialsDecorator } ) ) );

services.AddScheduledJobHandler<ReportJobHandler>( "report" );
services.AddSchedulingWorkers( new WorkerOptions { Claimant = workerIdentity, Workers = 2 } );
```

`SchedulerWorker` is unchanged by this: it is written against `IExecutionCoordinator` and never
learns which implementation it holds.

## Worker groups

Each `AddSchedulingWorkers` call registers a **group**: `Workers` workers sharing one `Claimant`
(the identity grants are qualified by) and, optionally, one `JobType` they alone claim. Every
worker attends a single job at once, so a slow job holds up only its own worker. A group may bring its
own coordinator, for instance a client carrying the group's own credentials:

```csharp
services.AddScheduledJobHandler<ReportJobHandler>( "report" );
services.AddScheduledJobHandler<CleanupJobHandler>( "cleanup" );

// Workers 1-2: any job type, using the host's IExecutionCoordinator and credentials.
services.AddSchedulingWorkers( new WorkerOptions { Claimant = "jobs-host-a", Workers = 2 } );

// Workers 3-6: reports only, under their own claimant and credentials.
services.AddSchedulingWorkers(
        new WorkerOptions { Claimant = "jobs-host-a-reports", Workers = 4, JobType = "report" },
        provider => new ExecutionCoordinatorClient(
                new HttpApiClient(
                        schedulerBaseUrl,
                        provider.GetRequiredService<IHttpClientFactory>(),
                        messageDecorators: new[] { reportCredentialsDecorator } ) ) );
```

Worker numbers run 1..N across all groups in registration order, and each worker holds its claims
as `claimant/number`. `Claimant` defaults to the machine name, so a restarted host presents the
same holders, re-takes its own claims and adopts its unfinished runs. **Two worker processes on one
machine must therefore set distinct claimants.** The management endpoints address workers by
number: `GET workers`, `GET workers/{worker}`, `POST workers/{worker}/pause|resume`.

## Authorization

Facades authorize; endpoints only require authentication and translate outcomes.

- **Schedules** — the owner rule first: the schedule's `Owner` may create their own schedules and
  view, modify, and delete them with no ACL grant. Everyone else falls through to role-based
  Pug.Authorized ACLs on `SecurityObjectTypes.Schedule`, qualified by domain. Registering on behalf
  of another owner is checked as `CREATE_ON_BEHALF` against the **owner principal** (its type and
  identifier as the object type and identifier) in the schedule's domain.
- **Execution** — `CoordinatorSecurityOperations` on `SecurityObjectTypes.ExecutionCoordinator`,
  qualified by the **claimant identity being acted as**, so one worker cannot claim or complete
  runs as another. A call arriving with no security principal at all is in-process by construction
  (endpoints reject unauthenticated requests before dispatch) and is trusted — an in-process worker
  needs no identity of its own.
- **Worker management** — `WorkerSecurityOperations` on `SecurityObjectTypes.SchedulerWorker`,
  qualified by the addressed worker's claimant; `GET workers` lists only the workers the caller
  may view. No owner exemption: this is an operator concern.

## Building

```sh
dotnet build Pug.Scheduling.slnx -warnaserror -m:1
dotnet test tests/Pug.Scheduling.Tests
```

Tests run against real SQLite stores and, through Testcontainers (Docker required; on this
machine `sg docker -c "dotnet test tests/Pug.Scheduling.Tests"`), PostgreSQL. Use
`--filter "Category!=Postgres"` without Docker.
