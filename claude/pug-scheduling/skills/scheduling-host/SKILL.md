---
name: scheduling-host
description: Use when hosting Pug.Scheduling's scheduler and execution coordinator in an application, i.e. wiring AddScheduling with SchedulerOptions and ExecutionCoordinatorOptions and an IdentifierGenerator, the SQLite or Postgres store and its DbUp migration in group order (RunGroupOrder, WAL), ISecurityManager and the grants (owner rule, CREATE_ON_BEHALF, claimant-qualified coordinator operations, worker management), schedulable job types and payload validators, MapSchedulingEndpoints / MapExecutionCoordinatorEndpoints with base paths and authorization policies and their status codes, the scheduling clock (DatabaseClock, TimeProviderClock), or choosing a topology (all-in-one, remote workers, API-only).
---

# Hosting Pug.Scheduling

Pug.Scheduling is a library. The host embeds two singleton facades: **`IScheduler`** (manage schedules) and **`IExecutionCoordinator`** (the only surface workers use to claim, renew, abandon and complete runs). Neither executes anything; workers do (see `scheduling-workers`). For the model and its guarantees see `scheduling-concepts`; for calling `IScheduler` see `scheduling-api`.

The host supplies what the library never chooses for itself:

1. a **store**, migrated (it is the only store: execution leases live on its schedule rows);
2. an **`ISecurityManager`** that resolves the current caller and decides grants;
3. an **`IdentifierGenerator`** for schedule and run identifiers;
4. **authentication** for the HTTP surface, and **logging** (`ILogger<T>`);
5. the **job types** it accepts, and optional payload validators.

## Packages

| Package | For |
|---|---|
| `Pug.Scheduling.DependencyInjection` | `AddScheduling`, `AddSchedulableJobType`, `AddJobPayloadValidator<T>`. Brings in `Pug.Scheduling` (the facades, the clocks, `ResourceQualifiers`), `Pug.Scheduling.Abstractions` and `Pug.Scheduling.Models` |
| `Pug.Scheduling.Data.Postgres` or `Pug.Scheduling.Data.SQLite` | The store, `DapperData` |
| The engine's ADO.NET provider | `Npgsql`, or `Microsoft.Data.Sqlite` (the full package: the SQLite store references only `Microsoft.Data.Sqlite.Core`, so the host supplies the native bundle) |
| `Pug.Scheduling.WebApi` | `MapSchedulingEndpoints`, `MapExecutionCoordinatorEndpoints`. Skip it for in-process-only use |
| `Pug.Scheduling.Worker.DependencyInjection` | Workers in this process (`scheduling-workers`) |
| `Pug.Scheduling.Worker.WebApi` | `MapSchedulerWorkerEndpoints`, only where workers run (`scheduling-workers`) |
| `Pug.Application.Security` | `SecurityManager`, unless you have your own `ISecurityManager`. The library references `Pug.Application.Security.Abstractions` 3.1.0 |
| `dbup-postgresql` / `dbup-sqlite` | Applying the schema (any migration tool works) |

The implementation packages target **net10.0**, so the host does too. The contracts (`Models`, `Abstractions`, `JobHandler.Abstractions`, `WebApi.Models`, `Client`) also target net6.0 and net8.0. Keep every `Pug.Scheduling.*` package at one version.

## A complete host (Postgres, workers in-process and remote)

```csharp
using DbUp;
using DbUp.Builder;
using DbUp.Engine;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Npgsql;
using Pug.Application.Data;
using Pug.Application.Security;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Data.Abstractions;
using Pug.Scheduling.DependencyInjection;
using Pug.Scheduling.WebApi;
using Pug.Scheduling.Worker;
using Pug.Scheduling.Worker.DependencyInjection;

WebApplicationBuilder builder = WebApplication.CreateBuilder( args );
string connectionString = builder.Configuration.GetConnectionString( "Scheduling" )!;

// 1. Store
Pug.Scheduling.Data.Postgres.DapperData store = new( connectionString, NpgsqlFactory.Instance );
builder.Services.AddSingleton<IApplicationData<IDataSession>>( store );

// 2. Security: who is calling (resolved per call), and what they may do. Yours, see "Security".
builder.Services.AddSingleton<ISecurityManager>( p => new SecurityManager(
	"Scheduling", p.GetRequiredService<IPrincipalIdentityAccessor>(), p.GetService<IPrincipalRoleProvider>(),
	p.GetRequiredService<IAuthorizationProvider>() ) );

// 3. Authentication for the endpoints
builder.Services.AddAuthentication( JwtBearerDefaults.AuthenticationScheme ).AddJwtBearer( o => builder.Configuration.Bind( "Jwt", o ) );
builder.Services.AddAuthorization();

// 4. The facades, and what may be scheduled here
builder.Services.AddScheduling( new GuidIdentifierGenerator() );
builder.Services.AddSchedulableJobType( "report" );
builder.Services.AddJobPayloadValidator<ReportPayloadValidator>( "report" );   // also marks "report" schedulable

// 5. Optional: workers in this process (scheduling-workers)
builder.Services.AddScheduledJobHandler<ReportJobHandler>( "report" );
builder.Services.AddSchedulingWorkers( new WorkerOptions { Workers = 2 } );

WebApplication app = builder.Build();

MigrateSchedulingSchema( connectionString, store );

app.UseAuthentication();
app.UseAuthorization();

app.MapSchedulingEndpoints();             // schedules/…
app.MapExecutionCoordinatorEndpoints();   // executionCoordinator/…, only for workers in other processes

app.Run();

sealed class GuidIdentifierGenerator : IdentifierGenerator
{
	public string GetNext() => Guid.NewGuid().ToString( "N" );
}
```

`MigrateSchedulingSchema` is under "Store and schema". The rest of this skill explains each piece.

## `AddScheduling` and its options

`AddScheduling( IdentifierGenerator identifierGenerator, SchedulerOptions options = null, ExecutionCoordinatorOptions coordinatorOptions = null )` registers, each with `TryAdd` (a registration made before it wins):

- `IScheduler` (`Scheduler`) and `IExecutionCoordinator` (`ExecutionCoordinator`), singletons;
- `IJobTypeRegistry`, fed by `AddSchedulableJobType` / `AddJobPayloadValidator<T>` in either order;
- `ISchedulingClock` as `DatabaseClock` (see "The clock").

It needs `IApplicationData<IDataSession>`, `ISecurityManager` and logging already registered. A web or generic host registers logging itself.

`SchedulerOptions` (`Pug.Scheduling.Abstractions`): defaults applied to a definition that omits them.

| Option | Default | Notes |
|---|---|---|
| `DefaultMisfirePolicy` | `FIRE_ONCE`, threshold 5 min | Used when `Definition.MisfirePolicy` is null |
| `DefaultRetryPolicy` | `MaximumAttempts = 3`, `Backoff = 1 min` | Used when `Definition.RetryPolicy` is null |
| `MinimumMisfireThreshold` | 15 s | The floor for every `MisfirePolicy.Threshold`, including the default's. Whole seconds, not negative. Stops a too-small threshold from turning every claim into a misfire (under `SKIP`, skipping nearly every occurrence) |
| `MinimumMisfireThreshold` | 15 s | The floor for every `MisfirePolicy.Threshold`, the default's included. Whole seconds, not negative. Stops a too-small threshold from turning every claim into a misfire (under `SKIP`, skipping nearly every occurrence) |
| `DefaultTimeout` | 10 min | Used when `Definition.Timeout` is zero (a negative timeout is refused). Above `TimerBounds.Maximum` (2^32−2 ms, about 49.7 days) the `Scheduler` constructor throws `ArgumentException` |

These defaults are copied into each definition and stored with it. Every schedule duration must be **whole seconds**; the `Scheduler` constructor throws `ArgumentException` for a default that is not, for a `DefaultMisfirePolicy` threshold below `MinimumMisfireThreshold`, or for a `DefaultTimeout` under 1 s. Definitions need an interval and a timeout of at least 1 s.

`ExecutionCoordinatorOptions` (`Pug.Scheduling.Abstractions`):

| Option | Default | Notes |
|---|---|---|
| `LeaseDuration` | 2 min | Validity of a claim, renewed by each worker heartbeat. Must comfortably exceed every worker's `HeartbeatInterval` (default 30 s) plus any forward step of the clock. Not validated |
| `ClaimCandidates` | 10 | Due schedules read per claim; a claim takes the first it can lock. Below 1, the coordinator throws `ArgumentException` when first resolved |

**Job types.** A definition whose `JobType` is not registered here is refused with `ArgumentException` (400). Registering a handler (`AddScheduledJobHandler<T>`) does **not** make a type schedulable. A host that accepts schedules for a type calls `AddSchedulableJobType( type )` or `AddJobPayloadValidator<T>( type )`, wherever the type executes.

**Payload validators.** `IJobPayloadValidator.ValidateAsync( ScheduleDefinition )` returns error strings. Any error refuses the registration or update with `ArgumentException` (400). It runs before the authorization check. It is registered as a **keyed singleton** under the job type.

## Store and schema

- **Register the store** as a singleton `IApplicationData<IDataSession>` (`Pug.Application.Data`; `IDataSession` from `Pug.Scheduling.Data.Abstractions`): `new Pug.Scheduling.Data.Postgres.DapperData( connectionString, NpgsqlFactory.Instance )`, or `new Pug.Scheduling.Data.SQLite.DapperData( connectionString, SqliteFactory.Instance )`.
- **The library never migrates.** `GetSchemaUpgradeScripts()` returns the scripts in order (schema version, then sequence). **DbUp sorts scripts by name**, and name order is not guaranteed to be script order (today's names happen to sort into a valid order; a later script need not). Give each its position as `RunGroupOrder`:

  ```csharp
  static void MigrateSchedulingSchema( string connectionString, IApplicationData<IDataSession> store )
  {
  	UpgradeEngineBuilder builder = DeployChanges.To.PostgresqlDatabase( connectionString )   // SqliteDatabase for SQLite
  		.JournalToPostgresqlTable( "public", "schedulingSchemaVersions" );                  // JournalToSqliteTable( … ) for SQLite

  	int order = 0;

  	foreach( SchemaUpgradeScript script in store.GetSchemaUpgradeScripts() )
  		builder = builder.WithScript( script.UpgradeScript.Name, script.UpgradeScript.Script,
  									new SqlScriptOptions { RunGroupOrder = order++ } );

  	DatabaseUpgradeResult result = builder.LogToConsole().Build().PerformUpgrade();

  	if( !result.Successful )
  		throw new InvalidOperationException( "Scheduling schema migration failed.", result.Error );
  }
  ```

  DbUp records scripts by name, and these names are generic (`Schedule table`, …), so a journal of the store's own avoids colliding with another component's scripts. The tables are plain-named (`schedule`, `scheduleRun`): give the store its own database or schema if the names could clash.
- **Postgres:** Npgsql enlists in the units' `TransactionScope`s itself; don't turn that off (`Enlist=false`). Several hosts may share one Postgres store: claims lock schedule rows (`for update skip locked`), and the database clock judges every lease.
- **SQLite:**
  - Enable WAL once after migrating (`PRAGMA journal_mode=WAL;`; it persists in the file). Every write unit takes the database write lock up front (`BEGIN IMMEDIATE`). In WAL mode, reads block no writer.
  - Writers wait for that lock. Microsoft.Data.Sqlite retries a busy database until the command timeout, `Default Timeout` in the connection string (30 s by default). `PRAGMA busy_timeout` affects only the connection that runs it.
  - `Microsoft.Data.Sqlite` 10.0.0 brings `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, which NuGet flags as vulnerable (NU1903). Reference a newer `SQLitePCLRaw.bundle_e_sqlite3` (3.0.3 or later) explicitly.
  - **SQLite is single-host.** Its clock is its host's clock (see "The clock"), and all writers share one lock. Use Postgres when several hosts share a store.

## Security: `ISecurityManager` and grants

The facades and the worker management endpoints ask `ISecurityManager.CurrentPrincipal.IsAuthorizedAsync( context, operation, NounQualifier resource, purpose )`. A refusal throws `NotAuthorized` (`Pug.Application.Security`), which is 403 over HTTP.

- **The facades are singletons**, so `CurrentPrincipal` must resolve the *current* caller each time it is read. `SecurityManager` does this through its `IPrincipalIdentityAccessor`.
- **The caller's identity** is `CurrentPrincipal.Identity.Identifier`. The `Scheduler` records it as `{ Type = "USER", Identifier }` on `Registration` / `LastUpdate`, and uses that as the default `Owner`, whatever kind of principal the caller is.
- **The `Scheduler` requires a principal.** It reads the caller's identity on every call, so a call without one fails. Background code that manages schedules must run under an explicit identity.
- **The coordinator skips its check when `CurrentPrincipal` is null** (current behaviour, issue #34). It treats a null principal as an in-process caller. An in-process worker therefore needs no identity or grants. **But** an `ISecurityManager` that returns a non-null principal for background work (an anonymous or "system" principal) is *checked*, and that principal must hold the coordinator grants.
- **That trust holds only if no request reaches the coordinator endpoints with a null principal.** A remote caller bypasses every coordinator grant when:
  - one of your `AuthorizationPolicies` doesn't require an authenticated user (policies given **replace** the default policy);
  - with no policies given, your `AuthorizationOptions.DefaultPolicy` doesn't require one;
  - something calls `.AllowAnonymous()` on the returned route group;
  - the caller is authenticated but your `ISecurityManager` can't map it to a principal and returns null.

  Every policy that guards the coordinator must require authentication, and your `ISecurityManager` must return a principal for every authenticated request.

What is checked (constants in `Pug.Scheduling.Abstractions`: `SecurityOperations`, `CoordinatorSecurityOperations`, `WorkerSecurityOperations`, `SecurityObjectTypes`; the qualifiers are built by `Pug.Scheduling.ResourceQualifiers`):

| Call | Operation(s) | Resource (`NounQualifier`) | Not checked when |
|---|---|---|---|
| List a domain's schedules | `LIST` | `{ Domain, Type = "SCHEDULE" }` | the `owner` filter is the caller as `USER` (`{ Type = "USER", Identifier = caller }`) |
| Read, update, delete, pause, resume, run-now, read runs | `GET_INFO`, `SET_DEFINITION`, `DELETE`, `PAUSE`, `RESUME`, `RUN_NOW`, `GET_RUN_HISTORY` | `{ Domain, Type = "SCHEDULE", Identifier = schedule }` | the caller owns it (**owner rule**: `Owner.Type == "USER"` and `Owner.Identifier` is the caller's) |
| Register for another owner | `CREATE_ON_BEHALF` | `{ Domain = definition's, Type = owner.Type, Identifier = owner.Identifier }` | the owner is omitted or is the caller. There is no `CREATE` operation: registering for oneself needs no grant |
| Claim, renew, abandon, complete | `CLAIM_JOBS`, `RENEW_CLAIM`, `ABANDON_CLAIM`, `REPORT_COMPLETION` | `{ Type = "EXECUTION_COORDINATOR", Identifier = claimant }`, no domain | `CurrentPrincipal` is null |
| Worker management | `GET_INFO`, `PAUSE`, `RESUME` | `{ Type = "SCHEDULER_WORKER", Identifier = claimant }`, no domain | never; no owner rule |

- **Coordinator grants are qualified by the claimant** being acted as, so one worker group's credentials cannot claim or complete runs as another's. Grant each remote worker host's service principal the four operations on its own claimant(s) (`scheduling-workers`).
- **Decide on `( resource.Type, operation )`, never on the operation alone.** `GET_INFO`, `PAUSE` and `RESUME` exist for both `SCHEDULE` and `SCHEDULER_WORKER`, and `CREATE_ON_BEHALF` arrives with the owner's type (`USER`, …) as its resource type.
- **Tenancy is yours:** `Domain` is on every schedule qualifier. Your provider decides which domains a principal may act in.
- **Payloads are not secret:** anyone holding `LIST` or `GET_INFO` on a schedule reads its `Payload`. Keep credentials out of it; store a reference the handler resolves.

## HTTP endpoints

```csharp
app.MapSchedulingEndpoints();                                   // schedules/…
app.MapExecutionCoordinatorEndpoints( new ExecutionCoordinatorEndpointOptions
{
	BasePath = "api/scheduling/coordinator",
	AuthorizationPolicies = new[] { "scheduling-workers" }      // registered by the host with AddAuthorization
} );
```

- **Options** (`SchedulingEndpointOptions`, `ExecutionCoordinatorEndpointOptions`, both in `Pug.Scheduling.WebApi`):
  - `BasePath` defaults to `schedules` or `executionCoordinator`. Leading and trailing `/` are ignored.
  - `AuthorizationPolicies` defaults to empty, which applies the default policy (an authenticated caller). Policies given are all required.
  - An empty base path or a blank policy name throws `ArgumentException` when mapping. An unregistered policy fails at request time.
- **The return value** is the endpoints' `RouteGroupBuilder`, for further conventions (rate limiting, CORS, OpenAPI). To mount under a further prefix, map onto a group: `app.MapGroup( "api" ).MapSchedulingEndpoints()`.
- **A policy is a transport gate, not the grant decision.** It decides who may call the API at all, and it replaces the default policy, so it must itself require an authenticated user (see "Security", issue #34). The facades still check every grant above, for in-process callers too. Keep the two consistent: a policy that admits a caller the provider refuses only moves the 403 later.
- **Clients take the same base path:** `new SchedulerClient( httpApiClient, "api/scheduling/schedules" )`, `new ExecutionCoordinatorClient( httpApiClient, basePath )`. A mismatch shows up as 404.
- **OpenAPI:** every endpoint carries a name, a tag and its response types. The library serves no document itself.

Routes, relative to each base path. With policies that require authentication (the default), an unauthenticated caller gets **401**; a refused policy or grant is **403**. Use a bearer or API-key scheme for these endpoints: a cookie scheme turns 401 and 403 into redirects to a login page.

| Route | Success | Errors |
|---|---|---|
| `GET schedules?domain=&ownerType=&ownerIdentifier=&jobType=` | 200 `ScheduleInfo[]` | 400 (`domain` missing, owner pair half-given) |
| `POST schedules?ownerType=&ownerIdentifier=` (body `ScheduleDefinition`) | 201, body the new identifier | 400 `BadRequestResultInfo` |
| `GET schedules/{identifier}` | 200 `ScheduleInfo` | 404 |
| `PUT schedules/{identifier}` (body `ScheduleDefinition`) | 200 | 400, 404 |
| `DELETE schedules/{identifier}` | 200 | 404 |
| `POST schedules/{identifier}/pause` · `/resume` | 200 | 404 |
| `POST schedules/{identifier}/run` | 202 (request recorded) | 404; 409 `RunInProgressResultInfo` while a run is in progress |
| `GET schedules/{identifier}/runs?from=&to=` | 200 `ScheduleRun[]` | 404 |
| `POST executionCoordinator/JobRunClaims` (body `JobRunClaimRequest { Claimant, Worker, JobType }`) | 200 `ClaimedJob`; 204 nothing due | 400 |
| `POST executionCoordinator/JobRuns/{run}/leaseRenewal?claimant=&worker=` | 200 `bool` (false: claim lost) | 400 |
| `POST executionCoordinator/JobRuns/{run}/abandonment?claimant=&worker=` | 200 `bool` (false: not held) | 400 |
| `POST executionCoordinator/JobRuns/{run}/completion?claimant=&worker=` (body `JobRunCompletion { Result, Outcome }`) | 200 | 400; 404 unknown run; 409 claim not held |

The exception mapping is fixed: `ArgumentException` 400 (`BadRequestResultInfo`), `NotAuthorized` 403, `UnknownScheduleException` / `UnknownRunException` 404, `RunInProgressException` / `ClaimNotHeldException` 409. `Pug.Scheduling.Client` maps them back, so remote callers see the same exceptions (401 stays the transport's `AuthenticationException`). The worker management routes are in `scheduling-workers`.

## The clock

Every time the facades decide on or record is read from **`ISchedulingClock`** inside the deciding transaction, after its row lock. That covers due-ness, misfire, lease expiry, retry and next-occurrence times, and run and audit timestamps.

- **`DatabaseClock`** (the default `AddScheduling` registers) reads the database's time: Postgres `clock_timestamp()`; SQLite `strftime( …, 'now' )`, which is the SQLite host's own clock. On Postgres, every coordinator host judges leases by one clock, so skew between hosts does not matter.
- **`TimeProviderClock( TimeProvider )`** is opt-in, for tests (with a fake `TimeProvider`) and single-host deployments. Register it as `ISchedulingClock`, before or after `AddScheduling`. With several hosts, each judges leases by its own clock: a host whose clock runs ahead treats live leases as lapsed and adopts runs that are still executing. `LeaseDuration` must then exceed the skew.
- **A registered `TimeProvider` is ignored by the facades.** Only an `ISchedulingClock` registration changes their clock. Workers use a registered `TimeProvider` only to stamp `WorkerInfo.LastPoll`.
- **Forward jumps:** on any clock, a forward step (an NTP correction on the database server, say) shortens every live lease by the size of the step. Keep `LeaseDuration` comfortably above `HeartbeatInterval` plus any step you expect.

## Topologies

| Topology | This host | Elsewhere |
|---|---|---|
| **All-in-one** | `AddScheduling`, job types, handlers and `AddSchedulingWorkers`; `MapSchedulingEndpoints` (+ `MapSchedulerWorkerEndpoints`). No coordinator endpoints. SQLite works | nothing |
| **Scheduler + coordinator, remote workers** | `AddScheduling`, job types; `MapSchedulingEndpoints` and `MapExecutionCoordinatorEndpoints`; a service identity per worker group, granted the coordinator operations on its claimant | worker hosts bind `ExecutionCoordinatorClient` (`scheduling-workers`) and reference no store |
| **API-only** | `AddScheduling`, job types; `MapSchedulingEndpoints` only, no workers | jobs execute in hosts on the same Postgres store with in-process workers, or in remote workers through a coordinator host |

These combine: several scheduler or coordinator hosts on one Postgres store are safe, because every transition locks the schedule row and judges by the database clock. On SQLite, keep everything in one host.

## Pitfalls

- **Scheduling a type that is not schedulable here.** Registration answers 400 "Job type '…' is not registered". Handler registration doesn't count, so add `AddSchedulableJobType` in every host that accepts schedules for the type.
- **Migrating without `RunGroupOrder`.** DbUp then applies scripts in name order, which is not guaranteed to be script order. Apply `GetSchemaUpgradeScripts()` in the order given, and use a journal of the store's own when the database is shared.
- **SQLite shared by several hosts.** Each host's clock judges the others' leases, and they contend for one write lock. Use Postgres.
- **A "system" principal for background work.** An in-process worker running under a non-null principal is checked against the coordinator grants, and every claim fails until that principal is granted them. Return null outside requests, or grant it.
- **Letting a null principal reach the coordinator endpoints** (issue #34). A policy or default policy that doesn't require authentication, `.AllowAnonymous()` on the group, or an `ISecurityManager` returning null for an authenticated caller lets a remote caller skip every coordinator grant.
- **Cookie authentication on the API.** Its 401 and 403 become redirects, which the clients can't map back to exceptions. Authenticate the endpoints with a bearer or API-key scheme.
- **Authorizing on the operation alone.** `GET_INFO`, `PAUSE` and `RESUME` are both schedule and worker operations. Match on the resource type as well.
- **Expecting a `TimeProvider` registration to move the facades' clock.** It doesn't; register `ISchedulingClock`.
- **`TimeProviderClock` on several hosts.** Skew adopts live runs. Keep `DatabaseClock` unless there is one host.
- **`LeaseDuration` shorter than the heartbeat plus a margin.** Leases lapse mid-run and a peer re-executes the occurrence. Remote workers' `HeartbeatInterval` is set in their hosts, so agree it with them.
- **Endpoints without authentication.** Map them only in a host with `AddAuthentication` / `AddAuthorization` and `UseAuthentication()` / `UseAuthorization()`. They refuse with `Results.Forbid()`, which needs an authentication scheme.
- **Mismatched base paths.** A client built with a base path other than the host's mapping gets 404 on every call.
- **Scoped services in a payload validator.** Validators are keyed singletons. Create a scope inside the validator if it needs scoped services.
