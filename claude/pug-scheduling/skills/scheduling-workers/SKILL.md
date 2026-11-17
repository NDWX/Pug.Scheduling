---
name: scheduling-workers
description: Use when hosting Pug.Scheduling workers, in the scheduler's process or in a separate worker host, i.e. AddSchedulingWorkers and worker groups, host-wide worker numbering, claimants and JobType filters, a remote worker binding ExecutionCoordinatorClient with per-group credentials and the coordinator grants it needs (CLAIM_JOBS, RENEW_CLAIM, ABANDON_CLAIM, REPORT_COMPLETION), WorkerOptions (PollInterval, HeartbeatInterval, CancellationGrace, MaxDetachedHandlers, AbandonTimeout), timeouts, heartbeats and leases, detached handlers, graceful shutdown and restart takeover, or the worker management endpoints (MapSchedulerWorkerEndpoints, WorkerInfo, pause and resume).
---

# Hosting Pug.Scheduling workers

A **worker** (`SchedulerWorker`, a `BackgroundService`) polls `IExecutionCoordinator` for a due run, creates the job type's handler, executes it while renewing the claim, and reports the outcome. It never touches a store, so it runs unchanged against the in-process coordinator or against a remote one over HTTP. **Each worker attends one job at a time.**

For writing handlers see `scheduling-job-handler`. For hosting the scheduler and coordinator see `scheduling-host`. For what a run, a lease and adoption mean see `scheduling-concepts`.

## Packages

| Host | Packages |
|---|---|
| **In-process** (beside `AddScheduling`) | `Pug.Scheduling.Worker.DependencyInjection` (brings `Pug.Scheduling.Worker` and `Pug.Scheduling.JobHandler.Abstractions`). The coordinator is the one `AddScheduling` registers. `Pug.Scheduling.Worker.WebApi` for the management endpoints |
| **Remote worker host** | `Pug.Scheduling.Worker.DependencyInjection` and `Pug.Scheduling.Client` (brings `Pug.HttpApiClient`), plus `Microsoft.Extensions.Http` for `AddHttpClient` and a credentials decorator, e.g. `ClientCredentialsDecorator` from `Pug.HttpApiClient.OAuth2`. **Not** `Pug.Scheduling`, `Pug.Scheduling.DependencyInjection`, any `Pug.Scheduling.Data.*` or `Pug.Scheduling.WebApi`: a remote worker reaches the scheduler only through the coordinator endpoints. These packages pull in no ASP.NET; add `Pug.Scheduling.Worker.WebApi` only if the host also serves worker management |
| **Handler libraries** | `Pug.Scheduling.JobHandler.Abstractions` only (`scheduling-job-handler`) |

The worker packages target net10.0.

## Registering workers: groups

```csharp
services.AddScheduledJobHandler<ReportJobHandler>( "report" );
services.AddScheduledJobHandler<CleanupJobHandler>( "cleanup" );

// Workers 1-2: any job type, through the IExecutionCoordinator in the container.
services.AddSchedulingWorkers( new WorkerOptions { Claimant = "jobs-host-a", Workers = 2 } );

// Workers 3-6: reports only, under their own claimant and credentials.
services.AddSchedulingWorkers(
	new WorkerOptions { Claimant = "jobs-host-a-reports", Workers = 4, JobType = "report" },
	p => new ExecutionCoordinatorClient( new HttpApiClient( schedulerUrl, p.GetRequiredService<IHttpClientFactory>(),
		messageDecorators: new IHttpRequestMessageDecorator[] { reportsCredentials } ) ) );
```

`AddSchedulingWorkers( WorkerOptions options = null, Func<IServiceProvider, IExecutionCoordinator> coordinator = null )`:

- **Call it once per group.** A group is `Workers` identical workers sharing a `Claimant` and an optional `JobType` filter.
- **Numbers are host-wide and deterministic:** 1..N across every group, in registration order. Each worker holds its claims as `claimant/number`.
- **Each worker is its own `IHostedService`.** The call also registers `ISchedulerWorkerRegistry` and the handler factory registry. Never register `SchedulerWorker` yourself, and never through `AddHostedService`: it deduplicates by type and would drop every worker after the first.
- **The coordinator factory** supplies the group's `IExecutionCoordinator`. It is invoked once per group, at startup, and the group's workers share the result. Without it, the group resolves `IExecutionCoordinator` from the container: the in-process one from `AddScheduling`, or a client you registered. A factory returning null throws `InvalidOperationException` at startup.
- **Invalid options throw `ArgumentException`** (naming the property) at the call, and the group consumes no numbers. The host must provide logging (`ILogger<SchedulerWorker>`).
- **One `JobType` per group**, or null for any type. To serve several types exclusively, register several groups.
- **Handlers are registered separately:** `AddScheduledJobHandler<T>( jobType )` (keyed scoped, one DI scope per run) or `AddScheduledJobHandlerFactory<T>( jobType )` (keyed singleton). Registering a handler does not make the type schedulable (`scheduling-host`). A claimed run whose type has no handler in this host is reported `FAILED` ("No handler registered…"), and the schedule's retry policy applies.

## A remote worker host

The worker binds `ExecutionCoordinatorClient` (`Pug.Scheduling.Client`) to the scheduler host's coordinator endpoints. `new ExecutionCoordinatorClient( IHttpApiClient httpClient, string basePath = "executionCoordinator" )`:

- `basePath` must match the host's `ExecutionCoordinatorEndpointOptions.BasePath`;
- the `HttpApiClient`'s message decorators carry the credentials;
- the client maps the endpoints' statuses back to the coordinator's exceptions.

A complete `Program.cs` is in [references/RemoteWorkerHost.cs](references/RemoteWorkerHost.cs). In outline:

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder( args );

builder.Services.AddHttpClient();
builder.Services.AddSingleton<IExecutionCoordinator>( p =>
{
	IHttpClientFactory http = p.GetRequiredService<IHttpClientFactory>();

	return new ExecutionCoordinatorClient( new HttpApiClient( schedulerUrl, http,
		messageDecorators: new IHttpRequestMessageDecorator[]
		{
			new ClientCredentialsDecorator( tokenEndpoint, clientId, clientSecret, scope, http )
		} ) );
} );

builder.Services.AddScheduledJobHandler<ReportJobHandler>( "report" );
builder.Services.AddSchedulingWorkers( new WorkerOptions { Claimant = "reports-host-a", Workers = 4, JobType = "report" } );

builder.Build().Run();
```

**Grants.** The coordinator checks each call against the caller's principal, qualified by the claimant acted as: `{ Type = "EXECUTION_COORDINATOR", Identifier = claimant }`. Grant each group's service principal all four operations (`CoordinatorSecurityOperations`) on its claimant:

| Operation | Used for | Without it |
|---|---|---|
| `CLAIM_JOBS` | every poll | each claim is refused: the worker claims nothing, shows `WorkerInfo.Fault` = `CLAIM_REFUSED`, logs one Error naming the grant, and polls every `RefusedPollRetryInterval` (1 min) until a claim is answered again |
| `RENEW_CLAIM` | every heartbeat | renewals are refused and retried each beat until the claim's deadline; then the claim is lost (Error naming the grant), the handler is cancelled and nothing is reported. Every run longer than a beat is executed again, once per lease plus `PollInterval` |
| `REPORT_COMPLETION` | reporting the outcome | the report is refused and retried every `PollInterval` until the lease or the run's timeout runs out, then dropped (Error naming the grant). The run is adopted and executed again, its side effects repeated, once per lease (or timeout) plus `PollInterval` |
| `ABANDON_CLAIM` | releasing the claim on shutdown | the release is refused (logged at Warning) and the run waits out its lease before a peer adopts it |

**The worker never pauses itself.** A refusal (403, `NotAuthorized`) is retried like any failure that did not get through, because the coordinator's security provider may be failing closed during an outage, which reads exactly like a missing grant; pausing then would leave every worker idle until someone resumed each by hand. The price of a grant that really is missing is the repeated executions in the table, so **alert on the Errors**, and grant the operation: the worker recovers by itself, with no resume.

Other failures are retried the same way: 401 (a token may refresh), 5xx, network errors, timeouts, 400. A renewal that keeps failing for the whole lease loses the claim (the handler is cancelled, nothing is reported); a report that keeps failing is dropped at the earlier of the lease and the run's timeout, and the run is executed again.

**Alert on these log lines (all Error):**
- `Worker {Claimant}/{Worker} (job type {JobType}) was refused a claim: …`: once per episode, then Information `… is no longer refused claims.` when it recovers. Meanwhile `WorkerInfo.Fault` is `CLAIM_REFUSED`.
- `Worker {Claimant}/{Worker} lost its claim on run {Run} of schedule {Schedule}: the coordinator refused its renewals …`.
- `Worker {Claimant}/{Worker} could not report completion of run {Run} of schedule {Schedule} before …`: an outcome was dropped; when refused, the line names `REPORT_COMPLETION`.

The claimant is part of the grant, so a group whose claimant changes per instance needs grants that cover every claimant it may use. An **in-process** worker running with no principal is not checked at all (`scheduling-host`, "Security").

## `WorkerOptions`

Validated by `WorkerOptions.Validate()`, at `AddSchedulingWorkers` and again by each worker. `TimerBounds.Maximum` is 2^32−2 ms, about 49.7 days.

| Option | Default | Valid | Meaning |
|---|---|---|---|
| `Claimant` | `Environment.MachineName` | not blank, no `/` | The group's identity: the coordinator grants are qualified by it, and the lease holder is `claimant/number` |
| `Workers` | 1 | ≥ 1 | Workers in the group |
| `JobType` | null (any type) | null or not blank | The only job type the group claims |
| `PollInterval` | 10 s | > 0, ≤ max | Wait after a poll that claimed nothing (or failed), after a run whose report was not recorded, and between retries of a completion report that did not get through. After a recorded run, the worker polls again at once |
| `RefusedPollRetryInterval` | 1 min | > 0, ≤ max | Wait after a refused claim, instead of `PollInterval`. Pause, resume or shutdown cut it short |
| `HeartbeatInterval` | 30 s | > 0, ≤ max | Claim renewal cadence while a run is attended. Capped at a third of the claim's window (`ClaimDuration` − 0.5 s), so even a failed renewal leaves a retry within the lease |
| `CancellationGrace` | 10 s | ≥ 0, ≤ max | How long a cancelled handler gets to stop before it is detached. Zero detaches at once |
| `MaxDetachedHandlers` | 2 | ≥ 1 | Detached handlers a worker tolerates before it stops claiming |
| `AbandonTimeout` | 5 s | > 0, ≤ max | Bound on releasing the claim during a graceful shutdown |

## Timeouts, heartbeat and lease

- **The run's timeout** is the schedule's (`ClaimedJob.Timeout`; `SchedulerOptions.DefaultTimeout`, 10 min, when the definition sets none). The worker enforces it and passes it to the handler. A timeout outside `0..TimerBounds.Maximum` (from a misbehaving remote coordinator) fails the run without starting the handler.
- **The lease is independent of the timeout.** A claim is valid for `LeaseDuration` (server side, default 2 min), handed to the worker as `ClaimedJob.ClaimDuration`. The worker renews it every `HeartbeatInterval` (capped as above), from the first beat after the claim until the run is reported or detached. A three-hour job holds a two-minute lease by heartbeats.
- **The worker times the claim itself.** Its deadline is the moment it sent the claim (or the last confirmed renewal) plus `ClaimDuration`, less a 0.5 s tolerance (`SchedulerWorker.LeaseTolerance`), on its own monotonic clock, so it gives up before the coordinator could hand the run to a peer. A claim whose `ClaimDuration` is under 1 s (`SchedulerWorker.MinimumClaimDuration`; the coordinator itself requires 10 s) fails the run without starting the handler.
  - A renewal that **fails** (network error, 401, 403, 5xx, timeout) is logged at Warning and retried at the next beat. If no renewal is confirmed before the deadline, the claim is **lost**. A hung renewal is waited on only until the deadline.
  - A renewal answered **false** means the claim is lost: a peer adopted the run, or the schedule was deleted.
  - On a lost claim the worker cancels the handler and abandons the run **without reporting**, even if the handler then finishes cleanly.
- **Reporting.** The worker does not claim again until the report is settled: recorded; dropped because the claim is no longer held; or failing (refused included) and retried every `PollInterval` until the earlier of the claim's deadline and the run's timeout, then dropped. Past the timeout the job counts as failed, so a `TIMED_OUT` report gets a single attempt. The host stopping ends the retries at once. After any report that was not recorded (and after an abandoned run), the worker waits `PollInterval` before claiming again: the run may still be its own, and an immediate claim would adopt it straight back. After a recorded report it polls at once.
- **When a run stops**, the worker cancels the handler's token and waits `CancellationGrace`. A run stops on its timeout, a lost claim or the host stopping. What is reported:

| The handler… | Reported |
|---|---|
| returns an outcome | `SUCCEEDED` / `FAILED` from `JobOutcome.Successful`. A null outcome is `FAILED` |
| throws `OperationCanceledException` after the timeout fired | `TIMED_OUT` (also when the host is stopping too) |
| throws `OperationCanceledException` after a shutdown | nothing: abandoned, and the claim is released |
| throws any other exception, timeout or not | `FAILED` |
| anything, after its claim was lost | nothing: the outcome is dropped |
| is still running after the grace | **detached**. `TIMED_OUT` on a timeout; on a lost claim or shutdown, nothing |

`scheduling-job-handler` has the full table, including factory failures.

## Detached handlers

A handler that ignores cancellation cannot be stopped, so the worker **detaches** it and moves on:

- it stops heartbeating and reports as above;
- it disposes the handler and its DI scope when the handler's task finally ends;
- it counts the handler in `WorkerInfo.DetachedJobs` (not `ActiveJobs`).

A detached handler **may overlap its own retry** or a peer's adoption of the run. At `MaxDetachedHandlers` the worker **stops claiming** and logs an Error once. It claims again as soon as one detached handler ends. Nothing bounds how long a detached handler runs, so a worker stuck at the limit stays idle until the process restarts. Fix the handler (`scheduling-job-handler`).

## Graceful shutdown

When the host stops a worker that is attending a run, its handler is cancelled and given `CancellationGrace`. Then:

- **The handler returned an outcome:** it is reported as usual.
- **The handler stopped by throwing on cancellation:** the run is abandoned without a report, and the worker **releases the claim** (`AbandonClaimAsync`, needs `ABANDON_CLAIM`). The release is bounded by `AbandonTimeout`, best effort, and never throws. A peer, or this host after a restart, adopts the run at its next poll instead of waiting out the lease, and executes the occurrence again.
- **The handler is still running:** it is detached and **nothing is released**, because a peer must not adopt a run that is still executing. Its lease lapses on its own, within `LeaseDuration`.

**The shutdown budget.** One worker may take up to `CancellationGrace + AbandonTimeout` (15 s by default) to stop, plus a completion report already in flight (bounded only by the coordinator transport's timeout). Keep that below the host's `HostOptions.ShutdownTimeout` (30 s by default). Retries of a report that is not getting through stop at once; the outcome is dropped and the claim is left to lapse (the coordinator was not answering, so a release would most likely fail too).

By default the generic host stops hosted services **one after another** (`HostOptions.ServicesStopConcurrently` is false):

- N busy workers can need N times that budget;
- workers not yet stopped keep claiming new runs meanwhile.

Hosts with several workers should stop them concurrently:

```csharp
builder.Services.Configure<HostOptions>( o =>
{
	o.ServicesStopConcurrently = true;
	o.ShutdownTimeout = TimeSpan.FromSeconds( 30 );   // above CancellationGrace + AbandonTimeout
} );
```

A worker cut off by the shutdown timeout leaves its run to lapse with its lease: it is safe, only slower.

## Restart takeover

A worker presenting the **same claimant and number** is the same lease holder. A restarted host therefore adopts its own unfinished runs at its first poll, even while their leases are still live, instead of waiting them out. Adoption re-executes the occurrence under the same run. It requires:

- **A stable claimant.** The default, the machine name, is stable on a VM. In containers it is usually the container's hostname, which changes per instance. Set `Claimant` explicitly if you want takeover there, and give **each replica its own** (for example a StatefulSet pod name or ordinal, `jobs-0`, `jobs-1`). A single fixed claimant shared by replicas is the next pitfall.
- **Stable numbering:** the same groups, in the same order, with the same `Workers`. Changing them only loses takeover for that restart; the old leases lapse normally.
- **Distinct claimants for concurrent processes.** Two live processes with the same claimant have identically numbered workers, which treat each other's live runs as their own and adopt them while they execute. Two worker processes on one machine must set different claimants.

## Worker management

`Pug.Scheduling.Worker.WebApi` manages the workers **hosted in the serving process**, addressed by worker number. Map it in the process that runs them. That process then needs authentication and an `ISecurityManager` (`scheduling-host`), remote worker hosts included.

```csharp
app.MapSchedulerWorkerEndpoints( new SchedulerWorkerEndpointOptions { BasePath = "ops/workers", AuthorizationPolicies = new[] { "operators" } } );
```

- **Options:** `BasePath` (default `workers`) and `AuthorizationPolicies` (default: your `DefaultPolicy`), as for the scheduler's endpoints. The call returns the `RouteGroupBuilder`.
- **An authenticated caller with a principal is always required.** No policy and no `.AllowAnonymous()` on the group lifts it: no authentication is 401, and an authenticated caller your `ISecurityManager` maps to no principal is 403. Use a bearer or API-key scheme, not cookies, which turn 401 and 403 into redirects.
- **These endpoints check the grants themselves:** `WorkerSecurityOperations` (`GET_INFO`, `PAUSE`, `RESUME`) on `{ Type = "SCHEDULER_WORKER", Identifier = the worker's claimant }`. There is no owner rule.

| Route | Success | Errors |
|---|---|---|
| `GET workers` | 200 `WorkerInfo[]`, only workers whose claimant the caller holds `GET_INFO` on (possibly empty) | 401, 403 (no principal or a refused policy) |
| `GET workers/{worker}` | 200 `WorkerInfo` | 401, 403, 404 unknown number |
| `POST workers/{worker}/pause` · `/resume` | 200 `WorkerInfo`, after the change | 401, 403, 404 |

`WorkerInfo` (`Pug.Scheduling.Models`):

| Field | Meaning |
|---|---|
| `Claimant`, `Worker` | Identity: the group's claimant and the host-wide number |
| `JobType` | The group's filter; null for any type |
| `Paused` | Whether an operator paused claiming. A worker never pauses itself |
| `Fault` | Why the coordinator is currently refusing the worker: `CLAIM_REFUSED` from a refused claim until a claim is answered again; null otherwise. Does not imply `Paused`, and clears by itself |
| `LastPoll` | UTC time of the last poll, null before the first. Informational |
| `ActiveJobs` | Runs being attended: 0 or 1 |
| `DetachedJobs` | Detached handlers still running. At `MaxDetachedHandlers` the worker is not claiming |

- **Pause** stops new claims; a run in progress continues to completion. It is held **in memory**: a restarted process starts unpaused. **Resume** lifts the pause only; a fault is not the worker's to clear. Pausing and resuming a refused worker cuts its `RefusedPollRetryInterval` wait short, so it claims again at once once the grant is in place. To stop a schedule rather than a worker, pause the schedule (`scheduling-api`).
- **In code:** `ISchedulerWorkerRegistry` (`Workers`, `Find( number )`) gives each `ISchedulerWorker` (`GetInfo()`, `Pause()`, `Resume()`). It performs no authorization of its own.
- There is no management client: call the routes with any HTTP client.

## Pitfalls

- **`AddHostedService` for workers.** It keeps only the first. `AddSchedulingWorkers` registers each worker correctly.
- **An any-type group (`JobType` null) in a host that lacks some handlers.** It claims runs it cannot execute, which fail and use up their retries. Give such hosts `JobType` groups for the types they handle.
- **Missing or revoked coordinator grants for a remote group.** Nothing pauses, so nothing stops: without `REPORT_COMPLETION` every run is executed again once per lease (or timeout) plus `PollInterval`, its side effects repeated; without `RENEW_CLAIM`, every run longer than a beat. Grant all four operations on each claimant, and alert on the Errors above.
- **A security provider that fails closed without logging it.** The worker cannot tell an outage from a missing grant; only the provider's own logs can. Make sure they say which it was.
- **Two processes sharing a claimant,** for example two services on one machine using the default. They adopt each other's live runs.
- **A `LeaseDuration` too short for the network.** The worker caps its beat at a third of its window, but renewals must still get through within the lease; near the 10 s minimum, two slow renewals lose the claim. Keep the scheduler host's `LeaseDuration` well above the worker's round trip.
- **Upgrading worker hosts before the scheduler hosts.** An older coordinator sends no `ClaimDuration`; the worker reads it as zero and fails every run it claims, unexecuted. Upgrade the scheduler first.
- **A coordinator transport without a timeout.** A single completion report is not bounded by the worker; it waits as long as the transport does (HttpClient's default is 100 s). Never set an infinite timeout on the worker's `HttpClient`.
- **A shutdown budget that ignores sequential stopping.** Several workers stopping one by one overrun `ShutdownTimeout`, and their claims lapse instead of being released. Set `ServicesStopConcurrently = true`.
- **Handlers that ignore cancellation.** They are detached, may overlap their own retry, and at `MaxDetachedHandlers` the worker stops claiming.
- **An `ISecurityManager` that returns a principal for background work in an in-process host.** The worker is then checked like a remote one, and every claim fails until that principal holds the coordinator grants.
- **A client base path that differs from the host's coordinator `BasePath`.** Every poll fails with 404, logged as a claim failure.
- **Pausing a worker to stop a schedule.** Other workers still claim it. Pause the schedule instead.
- **Referencing the scheduler packages in a remote worker host.** `AddScheduling` would register an in-process coordinator that needs a store. A remote host needs only the worker packages and the client.
