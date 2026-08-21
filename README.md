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
| `Pug.Scheduling.Abstractions` | `IScheduler`, `IExecutionCoordinator`, `IExecutionLeaseProvider`, `IScheduledJobHandler`, `ISchedulerWorker`, security constants. |
| `Pug.Scheduling` | The facades: `Scheduler` (management) and `ExecutionCoordinator` (execution). |
| `Pug.Scheduling.Data.Abstractions` · `.Data.Postgres` · `.Data.SQLite` | Schedule store contract and its Dapper implementations. |
| `Pug.Scheduling.Leases[.Data.Abstractions/.Postgres/.SQLite]` | Execution lease arbitration in its own store. |
| `Pug.Scheduling.Worker` | `SchedulerWorker` — the execution pump. |
| `Pug.Scheduling.DependencyInjection` | `AddScheduling`, `AddSchedulingWorker`, job type registration. |
| `Pug.Scheduling.WebApi.Models` | HTTP surface contracts shared by the endpoints and the clients. |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints()`, `MapExecutionCoordinatorEndpoints()`. |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints()` — separate so a worker host references nothing it does not need. |
| `Pug.Scheduling.Client` | `SchedulerClient` and `ExecutionCoordinatorClient` over the HTTP surfaces. |

## Concepts

A **schedule** is a recurrence (cron in an IANA time zone, or a fixed interval) plus a job type and
an opaque payload, owned by a principal. The scheduler computes the next occurrence; it never
executes anything.

A **worker** polls `IExecutionCoordinator` for due occurrences, dispatches each to an
`IScheduledJobHandler` keyed by job type, and reports the outcome. Due-ness, claim exclusivity,
misfire handling, retry scheduling, and next-occurrence computation all live behind the
coordinator — a worker sees none of it, and binds the same interface whether it runs in-process or
against a remote scheduler.

Exclusivity comes from `IExecutionLeaseProvider`, arbitrated in its own store. Leases are short
(2 minutes by default) and heartbeat-renewed, so a crashed worker's occurrence becomes claimable
again: execution is **at-least-once**, and handlers should key side effects on
`(Schedule, ScheduledFor)`.

## Hosting the scheduler

The host registers the two stores, then the framework:

```csharp
services.AddSingleton<IApplicationData<IDataSession>>(
        new Pug.Scheduling.Data.Postgres.DapperData( scheduleConnectionString, NpgsqlFactory.Instance ) );

services.AddSingleton<IExecutionLeaseProvider>(
        provider => new DatabaseExecutionLeaseProvider(
                new Pug.Scheduling.Leases.Data.Postgres.DapperData( leaseConnectionString, NpgsqlFactory.Instance ) ) );

// ISecurityManager comes from the host's own security stack.

services.AddScheduling( identifierGenerator );

services.AddScheduledJobHandler<ReportJobHandler>( "report" );      // executes here
services.AddJobPayloadValidator<ReportPayloadValidator>( "report" ); // optional, checked at registration

services.AddSchedulingWorker();   // omit where jobs execute elsewhere
```

Each store exposes `GetSchemaUpgradeScripts()`; run them with DbUp (or your migration tool of
choice) before first use. The schedule and lease stores may live in different databases.

Endpoints carry their own route prefixes, so map them at the root:

```csharp
app.MapSchedulingEndpoints();            // schedules/...
app.MapExecutionCoordinatorEndpoints();  // executionCoordinator/...   (only if workers run elsewhere)
app.MapSchedulerWorkerEndpoints();       // worker/...                 (only if a worker runs here)
```

## Hosting a worker out-of-process

The worker host references `Pug.Scheduling.Worker` and `Pug.Scheduling.Client` — no data stores, no
server-side packages — and binds the coordinator to its HTTP client:

```csharp
services.AddSingleton<IExecutionCoordinator>(
        provider => new ExecutionCoordinatorClient(
                new HttpApiClient(
                        schedulerBaseUrl,
                        provider.GetRequiredService<IHttpClientFactory>(),
                        messageDecorators: new[] { credentialsDecorator } ) ) );

services.AddScheduledJobHandler<ReportJobHandler>( "report" );
services.AddSchedulingWorker( new WorkerOptions { Claimant = workerIdentity } );
```

`SchedulerWorker` is unchanged by this: it is written against `IExecutionCoordinator` and never
learns which implementation it holds.

## Authorization

Facades authorize; endpoints only require authentication and translate outcomes.

- **Schedules** — the owner rule first: the schedule's `Owner` may create their own schedules and
  view, modify, and delete them with no ACL grant. Everyone else falls through to role-based
  Pug.Authorized ACLs on `SecurityObjectTypes.Schedule`, qualified by domain.
- **Execution** — `CoordinatorSecurityOperations` on `SecurityObjectTypes.ExecutionCoordinator`,
  qualified by the **claimant identity being acted as**, so one worker cannot claim or complete
  runs as another. A call arriving with no security principal at all is in-process by construction
  (endpoints reject unauthenticated requests before dispatch) and is trusted — an in-process worker
  needs no identity of its own.
- **Worker management** — `WorkerSecurityOperations` on `SecurityObjectTypes.SchedulerWorker`,
  qualified by claimant. No owner exemption: this is an operator concern.

## Building

```sh
dotnet build Pug.Scheduling.slnx -warnaserror -m:1
dotnet test tests/Pug.Scheduling.Tests
```

Tests run against real SQLite stores. The Postgres providers are build-verified here — their SQL
differs from the tested SQLite only in DDL types and boolean predicates — so CI should exercise
them with Testcontainers, in particular the lease arbitration and concurrent-coordinator races.
