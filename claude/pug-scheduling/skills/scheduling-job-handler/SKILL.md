---
name: scheduling-job-handler
description: Use when writing or reviewing a Pug.Scheduling job handler or handler factory, i.e. implementing IScheduledJobHandler or IScheduledJobHandlerFactory, reading ScheduledJobContext (Schedule, Run, Domain, Owner, Payload, ScheduledFor, Attempt, Trigger), honouring the timeout and cancellation token (and what detaching a handler means), making side effects idempotent on (Schedule, ScheduledFor), returning JobOutcome (Successful, Details, Error) or throwing, registering with AddScheduledJobHandler<T> or AddScheduledJobHandlerFactory<T>, making a job type schedulable (AddSchedulableJobType, AddJobPayloadValidator), DI scopes and disposal, or overlap with retries.
---

# Writing a Pug.Scheduling job handler

A job handler executes one run of one job type. The worker creates a handler per run through the job type's factory, calls `ExecuteAsync`, reports what it returns, and disposes it. The worker treats handlers as third-party code: it catches everything they throw and never relies on them stopping when asked.

Running a job somewhere else, such as calling a webhook or another service, is also just a handler.

## Packages

| Package | For |
|---|---|
| `Pug.Scheduling.JobHandler.Abstractions` | `IScheduledJobHandler`, `IScheduledJobHandlerFactory`. All a handler library needs. Targets `net6.0`, `net8.0` and `net10.0`, and depends only on `Pug.Scheduling.Models` (`ScheduledJobContext`, `JobOutcome`) |
| `Pug.Scheduling.Worker.DependencyInjection` | `AddScheduledJobHandler<T>`, `AddScheduledJobHandlerFactory<T>`, in the worker host (`net10.0`) |
| `Pug.Scheduling.DependencyInjection` | `AddSchedulableJobType`, `AddJobPayloadValidator<T>`, in the scheduler host |
| `Pug.Scheduling.Abstractions` | `IJobPayloadValidator` |

## The contract

```csharp
namespace Pug.Scheduling.JobHandler.Abstractions
{
	public interface IScheduledJobHandler
	{
		Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout, CancellationToken cancellationToken );
	}

	public interface IScheduledJobHandlerFactory
	{
		IScheduledJobHandler Create( ScheduledJobContext context );   // once per run
	}
}
```

`ScheduledJobContext` (`Pug.Scheduling.Models`):

| Field | Meaning |
|---|---|
| `Schedule` | The schedule's identifier |
| `Run` | This execution's `ScheduleRun` identifier, for correlating logs and output. An adopted run keeps it |
| `Domain` | The schedule's tenant |
| `Owner` | `Pug.Effable.Reference { Type, Identifier }`: the principal the job runs on behalf of |
| `JobType` | The job type this handler was created for |
| `Payload` | The schedule's JSON string, exactly as registered. It may be null |
| `ScheduledFor` | The occurrence being executed (UTC): the planned fire time, not the start time |
| `Attempt` | Zero-based retry attempt of that occurrence |
| `Trigger` | `RunTriggers`: `SCHEDULE`, `MISFIRE`, `RETRY` or `MANUAL` |

## A handler

```csharp
using Pug.Scheduling.JobHandler.Abstractions;
using Pug.Scheduling.Models;

public sealed class ReportJobHandler : IScheduledJobHandler
{
	private readonly IReportRenderer _renderer;
	private readonly IReportDeliveries _deliveries;   // yours: remembers what was already delivered

	public ReportJobHandler( IReportRenderer renderer, IReportDeliveries deliveries )
	{
		_renderer = renderer;
		_deliveries = deliveries;
	}

	public async Task<JobOutcome> ExecuteAsync( ScheduledJobContext context, TimeSpan timeout,
												CancellationToken cancellationToken )
	{
		// Every re-execution of this occurrence (adoption, retry, overlap) carries the same key.
		string key = $"{context.Schedule}/{context.ScheduledFor:O}";

		if( await _deliveries.FindAsync( key, cancellationToken ) is { } earlier )
			return new JobOutcome { Successful = true, Details = earlier.ReceiptJson };   // done by an earlier attempt

		ReportPayload payload = JsonSerializer.Deserialize<ReportPayload>( context.Payload ?? "null" )
								?? throw new InvalidOperationException( "The schedule has no report payload." );

		byte[] report = await _renderer.RenderAsync( context.Domain, context.Owner, payload, cancellationToken );
		string receiptJson = await _deliveries.DeliverOnceAsync( key, report, cancellationToken );

		return new JobOutcome { Successful = true, Details = receiptJson };
	}
}
```

## Registering

In the host that runs the workers:

```csharp
using Pug.Scheduling.Worker.DependencyInjection;

services.AddScheduledJobHandler<ReportJobHandler>( "report" );                    // keyed scoped: a fresh DI scope per run
services.AddScheduledJobHandlerFactory<WebhookJobHandlerFactory>( "webhook" );    // keyed singleton: your factory
services.AddSchedulingWorkers( new WorkerOptions { Workers = 2 } );               // see scheduling-workers
```

- **`AddScheduledJobHandler<T>( jobType )`** registers `T` as a keyed scoped `IScheduledJobHandler`. For each run it creates a new DI scope, resolves `T` from it, and disposes the scope when the run's handler is disposed. `T` may take scoped dependencies, such as a `DbContext`, and gets fresh ones every run.
- **`AddScheduledJobHandlerFactory<T>( jobType )`** registers your factory as a keyed singleton. Use it to choose or configure a handler per run from the context (tenant, owner, payload), or when the handler is not a DI service. The factory is shared by every worker at once, so keep it thread-safe and stateless. If its handlers need scoped services, the factory creates the scope and the handler disposes it.
- **One registration per job type.** Both methods register the job type's factory under the same key, and the last registration wins.
- Job type keys are case-sensitive, and must match `ScheduleDefinition.JobType` exactly.

### Schedulable versus executable

These are separate on purpose, because the API host that accepts schedules need not run the jobs:

| Concern | Registered with | Where | Without it |
|---|---|---|---|
| **Schedulable**: `RegisterAsync` accepts the job type | `AddSchedulableJobType( "report" )`, or `AddJobPayloadValidator<T>( "report" )`, which also marks it schedulable | The scheduler host (`AddScheduling`) | Registration fails with `ArgumentException`: "Job type 'report' is not registered." (400) |
| **Executable**: a worker can run it | `AddScheduledJobHandler<T>` or `AddScheduledJobHandlerFactory<T>` | Every host whose worker groups may claim the type | The run is reported `FAILED`, "No handler registered for job type 'report'.", and retried per policy |

A host that does both calls both. Registering a handler does not make a job type schedulable.

**Payload validators** run in the scheduler host at register and update time, after defaults are applied. They are keyed singletons. A non-empty result rejects the call with `ArgumentException` ("Schedule payload is not valid: …", 400). The whole definition is passed, so a validator can also check, for example, that a referenced resource belongs to `definition.Domain`.

```csharp
public sealed class ReportPayloadValidator : IJobPayloadValidator
{
	public Task<IEnumerable<string>> ValidateAsync( ScheduleDefinition definition ) =>
		Task.FromResult<IEnumerable<string>>(
			string.IsNullOrWhiteSpace( definition.Payload ) ? new[] { "A report payload is required." } : Array.Empty<string>() );
}

services.AddJobPayloadValidator<ReportPayloadValidator>( "report" );   // Pug.Scheduling.DependencyInjection
```

## Timeout and cancellation

- **`timeout`** is the schedule's `Definition.Timeout` (10 min unless the definition or host says otherwise). The worker enforces it on its own clock, from the moment it starts the run, factory included. Pass it on to anything that runs the job elsewhere.
- **`cancellationToken` fires** when:
  - the timeout elapses;
  - the host is stopping;
  - the claim is lost: a renewal answered that it is no longer held (another worker adopted the run after the lease lapsed, or the schedule was deleted), no renewal got through for the whole lease, or the coordinator refused the renewal (the worker then also pauses itself).
- **After it fires, the handler has `CancellationGrace`** (a `WorkerOptions` setting, 10 s by default) to end.
- **A handler still running after the grace is detached.** The worker stops attending it, reports `TIMED_OUT` (on a timeout) or nothing (on claim loss or shutdown), and claims new work. The detached handler runs on unattended, and is disposed when it finally ends. While it runs:
  - **it can overlap its own retry**, or a peer's re-execution of the same occurrence;
  - it occupies one of the worker's `MaxDetachedHandlers` slots (2 by default). At the limit the worker stops claiming.
- **To stop properly, let `OperationCanceledException` propagate.** Pass the token everywhere, call `ThrowIfCancellationRequested()` in loops, and don't catch, wrap or convert the exception. Then a timeout is reported as `TIMED_OUT`, and a shutdown abandons the run without spending an attempt (the claim is released, and the next worker re-executes the occurrence). A handler that catches the cancellation and returns `Successful = false` is recorded as `FAILED` instead, and uses up a retry.

## What gets reported

| The handler | Stop signal | Reported |
|---|---|---|
| Returns `Successful = true` | None, timeout or shutdown | `SUCCEEDED` |
| Returns `Successful = false` | None, timeout or shutdown | `FAILED`, retried per `RetryPolicy` |
| Returns null | Any but claim loss | `FAILED`, `Error` "Job handler returned no outcome." |
| Throws any exception other than `OperationCanceledException` | Any but claim loss | `FAILED`, `Error` = the exception's `ToString()` |
| Throws `OperationCanceledException` | None: its own, an `HttpClient` timeout for instance | `FAILED`, as above |
| Throws `OperationCanceledException` | Timeout | `TIMED_OUT`, retried per policy |
| Throws `OperationCanceledException` | Shutdown | Nothing. Abandoned, claim released, re-executed with the same run and attempt |
| Anything | Claim lost | Nothing. The outcome is dropped, since the run is no longer this worker's |
| Still running after the grace | Timeout | `TIMED_OUT`; detached |
| Still running after the grace | Shutdown or claim lost | Nothing; detached, and its lease is left to lapse |
| Factory missing, throwing, or returning null | | `FAILED`; the handler never runs |

`JobOutcome.Details` is your optional JSON (a receipt, an output reference) and `Error` your failure text. Both are stored verbatim in run history, which anyone with `GET_RUN_HISTORY` on the schedule can read.

## Idempotency: key on `(Schedule, ScheduledFor)`

Execution is **at-least-once**. The same occurrence runs again after a worker crash or a lost report (an adoption, with the same `Run` and `Attempt`), after a failure (a retry, with a new `Run` and `Attempt + 1`), and possibly at the same time as a detached or partitioned predecessor.

- Key every external side effect (an email, a downstream write, a file name) on `(Schedule, ScheduledFor)`. Format `ScheduledFor` invariantly (`:O`). It is UTC.
- **Don't put `Attempt` or `Run` in the key.** A retry must find what an earlier attempt already did.
- When the work turns out to be done already, **return `Successful = true`**, so the schedule advances. A failure would retry, then record the occurrence as failed.
- Distinct occurrences have distinct keys. A `MANUAL` run's `ScheduledFor` is the time it was claimed, so it never collides with a scheduled occurrence. The exception is a manual run during a retry's backoff, which carries the retried occurrence. Each `RUN_ALL` catch-up run has its own key.
- Make the "already done?" check and the side effect safe under concurrency (a unique key, a conditional write), not just sequentially.

## Owner and security context

The worker runs handlers off its poll loop, outside any request, with **no ambient principal**. `context.Owner` is who the job runs for. A handler doing security-sensitive work must establish that principal itself, for example by building an `ISecurityManager` for it or by calling downstream APIs as the owner, so that data access is authorized as the owner rather than as a system identity. Pug.Scheduling does not impersonate anyone.

## Disposal

- The worker disposes the handler once `ExecuteAsync` has ended, through `IAsyncDisposable` if implemented, otherwise `IDisposable`. This happens exactly once, even for a detached handler (when it finally ends) and when `ExecuteAsync` threw. A failure in disposal is logged and never changes the outcome.
- With `AddScheduledJobHandler<T>`, disposal disposes the run's DI scope, which disposes `T` and the scoped and transient services it created.
- With your own factory, release per-run resources in the handler's `Dispose`/`DisposeAsync`. The factory is never disposed per run.

## Pitfalls

- **Ignoring the token is not "safe, just slow".** A detached handler overlaps its own retry, and enough of them stop the worker claiming at all.
- **Converting cancellation into a failed outcome spends retries.** On shutdown it records `FAILED`, where the run should have been abandoned and re-executed.
- **Side effects keyed on `Run`, or on "now", duplicate on re-execution.** Use `(Schedule, ScheduledFor)`.
- **A worker group without `JobType` claims every type.** Each host running such a group needs a handler for every schedulable type, or its group needs a `JobType` filter. Otherwise it claims runs it can only fail.
- **A factory singleton holding per-run state** breaks under concurrent workers. Per-run state belongs in the handler.
- **Handler constructors run per run**, inside the measured timeout. Keep them cheap and free of I/O.
- **Synchronous blocking** cannot observe cancellation, so a blocked handler is always detached. Use async I/O that takes the token.
- **Exception text lands in run history.** `Error` holds the full `ToString()`, so keep secrets out of exception messages.
- **The payload is user data.** Validate it at registration with an `IJobPayloadValidator`, and still parse it defensively in the handler: the definition can change between runs.
- **The payload is not secret.** Anyone with `LIST` or `GET_INFO` on the schedule can read it. Put references to secrets in it, and resolve them in the handler.
- **A report that cannot be delivered re-executes the handler.** The worker retries it until the lease runs out, then drops the outcome; the run is adopted and executed again, without using up an attempt (at most once per lease). A refused report (missing `REPORT_COMPLETION`) pauses the worker instead, and the run is executed again once it is resumed. Idempotency keeps both harmless.
- **There is no current user.** Code that calls `IScheduler` or other authorized services from a handler must run as the owner (or another explicit principal).
