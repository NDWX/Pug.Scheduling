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

A worker times its claim itself: each claim carries `ClaimDuration` (the coordinator's
`LeaseDuration`), and the worker treats a claim no renewal confirmed within that duration, less a
0.5 s tolerance, as lost: it cancels the handler and reports nothing. It claims nothing else until
the run's report is settled, retrying one that does not get through until the lease runs out. A
success it could not get recorded is remembered by occurrence (`UnrecordedSuccesses`, shared by the
host's workers on one coordinator, in memory, 24 h and 1,000 entries at most) and **replayed** when
the occurrence is claimed again on that host: the outcome is reported, the handler is not run.
Handlers must still be idempotent on `(Schedule, ScheduledFor)`: a restart or another host runs the
job again. **The worker never pauses itself**: a coordinator refusal (403) is retried like any other
failure, since a security provider failing closed during an outage refuses exactly as a missing grant
does. A refused renewal is retried until the claim's deadline, a refused report until the lease runs
out, and a refused claim every `WorkerOptions.RefusedPollRetryInterval` (1 min), shown meanwhile as
`WorkerInfo.Fault` = `CLAIM_REFUSED`. Each case logs an Error naming the grant: alert on them, since a
grant that really is missing keeps the occurrence claimed (and, for `RENEW_CLAIM`, cuts and repeats
every run longer than the window, the lease less 0.5 s) until it is granted. After a run whose report was not recorded, the
worker waits `PollInterval` before claiming again, so it does not adopt its own run straight back.

A single completion report is bounded only by the coordinator's transport and store, so keep their
timeouts finite (HttpClient's 100 s default, Npgsql's 30 s command timeout, SQLite's busy timeout),
and allow for one report in flight in the worker host's shutdown budget.

**Upgrading:** upgrade the scheduler (coordinator) hosts before the worker hosts. A coordinator from
before #35 sends no `ClaimDuration`, which a new worker reads as zero, and it then fails every run it
claims without executing it.

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
// WorkerOptions also sets Claimant, JobType, PollInterval, RefusedPollRetryInterval
// (default 1 min), HeartbeatInterval, CancellationGrace (default 10 s),
// MaxDetachedHandlers (default 2) and AbandonTimeout (default 5 s).
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
a renewal's round trip plus any such step (workers renew at least three times per window, the
lease less 0.5 s). `LeaseDuration` is at least 10 s (`ExecutionCoordinatorOptions.MinimumLeaseDuration`)
and must be the same on every coordinator instance over one store: a renewal restarts the full
duration the worker was handed.

**Time zones.** `Recurrence.TimeZone` must be an IANA identifier (`Australia/Melbourne`); a Windows ID
(`AUS Eastern Standard Time`) is refused at registration and update with `ArgumentException` (400).
The zone is looked up again at every completion, on the coordinator's host, so every host running the
`Scheduler` or the coordinator needs ICU and an up-to-date IANA time-zone database: on Linux
containers install `libicu` and `tzdata`, and do not enable `InvariantGlobalization`. A host that
cannot resolve a schedule's zone fails every completion of it.

**SQLite host requirements.** Enable WAL journal mode (`PRAGMA journal_mode = WAL`) and a busy
timeout: units of work run as real transactions (a write begins `IMMEDIATE`), and under WAL readers
do not block the writer. Set the busy timeout in the connection string with Microsoft.Data.Sqlite's
`Default Timeout` keyword (seconds), e.g. `Data Source=app.db;Default Timeout=5`; a
`PRAGMA busy_timeout` run on another connection does not apply to the store's connections. Apply the
schema scripts in group order, as above.

Microsoft.Data.Sqlite 10.0.0 pulls in SQLitePCLRaw 2.1.11, which carries a vulnerability advisory
(NU1903). Reference `SQLitePCLRaw.bundle_e_sqlite3` 3.0.3 or later in the host, as the tests do.

**Durations and `SchedulerOptions`.** Every schedule duration (`Recurrence.Interval`,
`MisfirePolicy.Threshold`, `RetryPolicy.Backoff`, `Timeout`) must be a whole number of seconds, or
registration and definition updates fail with `ArgumentException` (400). The interval must be at
least 1 s; a `Timeout` of zero means "omitted" and takes `DefaultTimeout`; a timeout is otherwise at
least 5 s and at most `TimerBounds.Maximum`; a backoff is not negative. A `MisfirePolicy.Threshold`
must be at least `SchedulerOptions.MinimumMisfireThreshold` (default 15 s, whole seconds, not
negative): a threshold shorter than a claim takes to arrive would turn every claim into a misfire
and, under `SKIP`, skip nearly every occurrence. The `Scheduler` constructor validates the options
and throws `ArgumentException` for a sub-second or negative minimum, a `DefaultMisfirePolicy`
threshold that is below the minimum or not whole seconds, a `DefaultRetryPolicy` backoff that is not
whole seconds, or a `DefaultTimeout` that is not whole seconds or is under 5 s.

**Status codes.** The facades' exceptions map to the endpoints' responses, and the clients map them
back:

| Response | Exception | Raised by |
|---|---|---|
| 400 `BadRequestResultInfo` | `ArgumentException` | invalid definition, including a sub-second duration or a `Timeout` over `TimerBounds.Maximum` (about 49.7 days) |
| 403 | `NotAuthorized` | any operation without the grant |
| 404 | `UnknownScheduleException` / `UnknownRunException` | unknown schedule or run |
| 409 `RunInProgressResultInfo` | `RunInProgressException` | run-now while a run is active |
| 409 | `ClaimNotHeldException` | completion by a worker that does not hold the run |

401 stays the transport's `AuthenticationException`; any other failure status (a 503 from a proxy,
say) surfaces from the clients as `HttpApiRequestException`, never as success.

Endpoints carry their own route prefixes, so map them at the root. Each call takes optional options for
the API's base path and authorization policies, and returns the endpoints' route group:

```csharp
app.MapSchedulingEndpoints();            // schedules/...
app.MapExecutionCoordinatorEndpoints();  // executionCoordinator/...   (only if workers run elsewhere)
app.MapSchedulerWorkerEndpoints();       // workers/...                (only if workers run here)

// A different base path, and a policy (registered by the host with AddAuthorization) gating the API.
// The policy narrows who may call the API at all; the facades still enforce the grants.
app.MapSchedulingEndpoints( new SchedulingEndpointOptions
{
    BasePath = "api/v1/schedules",
    AuthorizationPolicies = new[] { "scheduling-api" }
} );
```

With no policies given, an API applies the host's `DefaultPolicy`; policies given are required
together with an authenticated user. Either way, an endpoint filter guarantees an authenticated
caller whom the host's `ISecurityManager` maps to a principal, regardless of the policies: neither a
permissive policy nor `AllowAnonymous()` on the returned group admits anyone else. An
unauthenticated caller is challenged (401); an authenticated one with no principal is forbidden
(403). The filter resolves `ISecurityManager`, so a host mapping the scheduling or coordinator
endpoints (a gateway, say) must register one; without it every request is a 500.

`SchedulerClient` and `ExecutionCoordinatorClient` take the matching base path as an optional second argument
(`new SchedulerClient( httpClient, "api/v1/schedules" )`); without one they use the defaults.

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
number: `GET workers`, `GET workers/{worker}`, `POST workers/{worker}/pause|resume`. `WorkerInfo`
reports `Paused` (an operator's) and `Fault` (`CLAIM_REFUSED` while the coordinator refuses its
claims; it clears by itself).

## Authorization

Facades authorize; endpoints only require an authenticated caller with a principal and translate
outcomes.

- **Schedules** — the owner rule first: the schedule's `Owner` may create their own schedules and
  view, modify, and delete them with no ACL grant. Everyone else falls through to role-based
  Pug.Authorized ACLs on `SecurityObjectTypes.Schedule`, qualified by domain. Registering on behalf
  of another owner is checked as `CREATE_ON_BEHALF` against the **owner principal** (its type and
  identifier as the object type and identifier) in the schedule's domain.
- **Execution** — `CoordinatorSecurityOperations` on `SecurityObjectTypes.ExecutionCoordinator`,
  qualified by the **claimant identity being acted as**, so one worker cannot claim or complete
  runs as another. A call arriving with no security principal at all is taken for an in-process
  one and is trusted — an in-process worker needs no identity of its own. Under no supported
  configuration does a call through `MapExecutionCoordinatorEndpoints` arrive that way: their
  endpoint filter refuses a caller without authentication or without a principal, whatever the
  policies admit and even under `AllowAnonymous()`.
  **Warning:** any other transport you put in front of the coordinator — your own controller, a gRPC
  service, a message consumer — must establish the caller's principal before calling
  `IExecutionCoordinator`. Otherwise the coordinator takes its calls for in-process ones and skips
  every grant.
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
