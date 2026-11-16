---
name: scheduling-api
description: Use when an application creates or manages Pug.Scheduling schedules, i.e. calling IScheduler in-process or over HTTP with SchedulerClient (and its basePath) to register a schedule (ScheduleDefinition, cron with an IANA time zone or an interval, NotBefore/NotAfter, misfire and retry policies, timeout and their defaults), register on behalf of another owner (CREATE_ON_BEHALF), list, read, update a definition (and when NextRun moves), delete, pause, resume, run now (409 RunInProgressException / RunInProgressResultInfo while a run is in progress) or read run history, or when handling its exceptions, HTTP status codes, SecurityOperations and the owner rule.
---

# Managing schedules with `IScheduler`

`IScheduler` (`Pug.Scheduling.Abstractions`) is the management API: configuration and observation only. Nothing it does executes a job. Run-now only raises a request that a worker claims. It has two implementations with **the same contract, exceptions included**:

- **in-process**, `Scheduler`, when your application hosts the scheduler (`AddScheduling`; see `scheduling-host`);
- **over HTTP**, `SchedulerClient` (`Pug.Scheduling.Client`), against a host that maps `MapSchedulingEndpoints()`.

Code against the interface and choose the implementation in DI. For what the states and triggers mean, see `scheduling-concepts`.

## Packages

| Package | For |
|---|---|
| `Pug.Scheduling.Abstractions` | `IScheduler`, `UnknownScheduleException`, `RunInProgressException`, `SecurityOperations`, `SchedulerOptions`, `TimerBounds` |
| `Pug.Scheduling.Models` | `ScheduleDefinition`, `Recurrence`, `MisfirePolicy`, `RetryPolicy`, `ScheduleInfo`, `ScheduleRun`, and the string constants `MisfireHandlings`, `RunTriggers`, `RunResults`. Brings in `Pug.Effable`, whose `Reference` is the owner type (`using Pug.Effable;`) |
| `Pug.Scheduling.Client` | `SchedulerClient`, on `Pug.HttpApiClient`. Targets `net6.0`, `net8.0` and `net10.0` |
| `Pug.Scheduling.WebApi.Models` | `RunInProgressResultInfo` and `BadRequestResultInfo`, for callers reading raw HTTP responses |

`NotAuthorized` comes from `Pug.Application.Security.Abstractions` (`using Pug.Application.Security;`). Keep every `Pug.Scheduling.*` package at one version.

## Calling a remote scheduler

```csharp
services.AddHttpClient();
services.AddSingleton<IScheduler>( p => new SchedulerClient(
	new HttpApiClient(
		new Uri( "https://scheduler.example.com/" ),   // the host, with any path it is served under
		p.GetRequiredService<IHttpClientFactory>(),
		messageDecorators: new IHttpRequestMessageDecorator[] { credentialsDecorator } ),   // carries the caller's credentials
	"schedules" ) );   // the host's SchedulingEndpointOptions.BasePath; omit for the default, "schedules"
```

- **`basePath`** must match where the host mapped the API. Leading and trailing `/` are ignored, and it is relative to the base address. An empty one throws `ArgumentException`.
- **Credentials** go in the `HttpApiClient`'s decorators. The server authorizes as the principal the token identifies, owner rule included.
- **Raw HTTP** routes, bodies and status codes are in [references/http.md](references/http.md).

In-process, inject `IScheduler`. **Every call needs a current principal** (`ISecurityManager.CurrentPrincipal`), because the scheduler derives the default owner, the owner rule and the audit stamps from it. Code running outside a request, such as a background job, must run under an explicit principal.

## Registering a schedule

```csharp
using Pug.Effable;
using Pug.Scheduling.Abstractions;
using Pug.Scheduling.Models;

string id = await scheduler.RegisterAsync( new ScheduleDefinition
{
	Domain = "tenant-42",
	Name = "Weekly sales report",
	JobType = "report",
	Payload = JsonSerializer.Serialize( new { report = "sales", format = "pdf" } ),
	Recurrence = new Recurrence { CronExpression = "0 7 * * MON", TimeZone = "Australia/Melbourne" },
	MisfirePolicy = new MisfirePolicy { Handling = MisfireHandlings.FireOnce, Threshold = TimeSpan.FromMinutes( 30 ) },
	RetryPolicy = new RetryPolicy { MaximumAttempts = 3, Backoff = TimeSpan.FromMinutes( 5 ) },
	Timeout = TimeSpan.FromMinutes( 15 )
} );
```

| `ScheduleDefinition` | Rules |
|---|---|
| `Domain` | Required. The tenant scope of every check and listing. **Immutable** after registration |
| `Name` | Required. Listings are ordered by it |
| `JobType` | Required, and declared schedulable on the scheduler host (`AddSchedulableJobType` or `AddJobPayloadValidator`), or the call is refused with `ArgumentException`. Case-sensitive |
| `Payload` | Optional. A **string** holding JSON, opaque to the scheduler, handed to the handler unchanged. The job type's `IJobPayloadValidator`, if the host registered one, checks it at register and update |
| `Recurrence` | Required; see below |
| `MisfirePolicy` | Optional. `Handling` is `FIRE_ONCE`, `SKIP` or `RUN_ALL`. `Threshold` must not be negative |
| `RetryPolicy` | Optional. `MaximumAttempts` ≥ 1 counts the first attempt, so 1 means no retries. `Backoff` must not be negative; retry *n* waits `Backoff × 2^(n−1)` |
| `Timeout` | Optional. Zero means "use the default". Must not be negative or exceed `TimerBounds.Maximum` (about 49.7 days). The worker cancels the run when it elapses |

**Defaults.** Omitted policies and a zero timeout get the host's `SchedulerOptions` at registration, and the stored definition shows the values applied:

| `SchedulerOptions` | Default |
|---|---|
| `DefaultMisfirePolicy` | `FIRE_ONCE`, threshold 5 min |
| `DefaultRetryPolicy` | 3 attempts, backoff 1 min |
| `DefaultTimeout` | 10 min |
| `MinimumMisfireThreshold` | 15 s (floor for `Threshold`) |

Durations (`Interval`, `Threshold`, `Backoff`, `Timeout`) must be **whole seconds** (see Pitfalls). The schedule starts enabled, unpaused, with its first occurrence computed at the scheduler's clock.

### Recurrence

Exactly one of `CronExpression` and `Interval` must be set:

- **Cron:** standard five-field Cronos syntax (minute, hour, day of month, month, day of week), with no seconds field. It is evaluated in `TimeZone`, so occurrences follow local time across DST. A local time skipped by DST fires at the jump, and an ambiguous one fires once.
- **Interval:** a positive `TimeSpan` (in practice, at least 1 s). Occurrences stay on a fixed grid (`anchor + k × Interval`) and never drift with late completions.
- **`TimeZone`** is required for both modes. It is an IANA identifier such as `Australia/Melbourne`, resolved by the scheduler host's time-zone database. An unknown one is an `ArgumentException`.
- **`NotBefore` / `NotAfter`** are an optional window, UTC, inclusive at both ends. `NotBefore` must be strictly before `NotAfter`. Pass `DateTimeKind.Utc` values: the calculator treats any `DateTime` as UTC without converting it.

The first `NextRun` (at registration, or when an idle schedule's recurrence changes):

| Recurrence | First occurrence |
|---|---|
| Cron, `NotBefore` in the future | The first cron time at or after `NotBefore` |
| Cron, otherwise | The first cron time after now |
| Interval with `NotBefore` | The first `NotBefore + k × Interval` (k ≥ 0) at or after now. `NotBefore` anchors the grid |
| Interval without `NotBefore` | now + `Interval` |
| Any result later than `NotAfter` | None: `NextRun` is null and the schedule is exhausted |

An exhausted schedule is kept, with its history. It runs again only through run-now, or after a definition update that moves the window.

### Owner, and registering on behalf

- `RegisterAsync( definition )` makes the caller the owner: `new Reference { Type = "USER", Identifier = <caller's identifier> }`. This needs **no grant**.
- `RegisterAsync( definition, owner )`, for any other owner, needs **`CREATE_ON_BEHALF`** on the owner principal in the definition's domain. The checked resource is `{ Domain = definition.Domain, Type = owner.Type, Identifier = owner.Identifier }`, so a caller is granted the right to act for specific principals, per domain.
- Over HTTP, the owner travels as `?ownerType=…&ownerIdentifier=…`. Giving only one of them gets 400.
- The job runs on behalf of `Owner`: handlers receive it as `ScheduledJobContext.Owner`. The audit stamps (`Registration`, `LastUpdate`) record the caller.

## Operations

| Call | Returns | Notes |
|---|---|---|
| `RegisterAsync( definition, owner = null )` | The new identifier | See above |
| `GetSchedulesAsync( domain, owner = null, jobType = null )` | `Task<IEnumerable<ScheduleInfo>>`, ordered by name | Always one domain. The owner filter matches both the owner's `Type` and `Identifier`, and both must be non-blank (`ArgumentException` otherwise) |
| `GetScheduleAsync( identifier )` | `ScheduleInfo` | Includes `State` and, while a run is in progress, `ExecutionLease` |
| `UpdateDefinitionAsync( identifier, definition )` | | Replaces the whole definition; see below |
| `DeleteAsync( identifier )` | | Removes the schedule and its run history |
| `PauseAsync( identifier )` / `ResumeAsync( identifier )` | | Stops or restarts claiming. A run already claimed continues |
| `RunNowAsync( identifier )` | | Requests a `MANUAL` run; see below |
| `GetRunsAsync( identifier, from = null, to = null )` | `Task<IEnumerable<ScheduleRun>>`, most recent first | Bounded inclusively by `Started`. Includes in-progress runs (`Finished` null) and `SKIPPED` runs (`Outcome` null). No paging |

### Updating a definition

`UpdateDefinitionAsync` **replaces the whole definition** and validates it as registration does, payload validator included.

- **Omitted policies and a zero `Timeout` reset to the service defaults**, not to the schedule's current values. Read, modify and write back:

  ```csharp
  ScheduleInfo current = await scheduler.GetScheduleAsync( id );

  await scheduler.UpdateDefinitionAsync( id, current.Definition with
  {
  	Recurrence = current.Definition.Recurrence with { CronExpression = "0 8 * * MON" }
  } );
  ```

- **A different `Domain` is refused** (`ArgumentException`). `JobType`, `Payload` and everything else may change.
- **When `NextRun` moves:** only when the `Recurrence` changed (any of its fields, the window included) **and** the schedule is idle, with no run in progress and no retry pending. It is then recomputed from now, as at registration. Otherwise `NextRun` is kept. A run in progress applies the new recurrence when it completes, and a pending retry keeps its time.
- Pause state, a pending run-now, a run in progress and a pending retry are all left as they are.

### Pause, resume and delete

- **Pause** stops new claims of the schedule: occurrences, retries, run-now and adoption of a crashed run all wait. **Resume** only lifts the pause. The next claim applies the misfire policy to whatever elapsed meanwhile, so a long pause under `FIRE_ONCE` produces one `MISFIRE` run.
- **Delete** removes the schedule and all its runs at once. A run in progress is not stopped by the call, but its worker's next heartbeat is refused, so the handler is cancelled as a lost claim, and its completion is recorded nowhere.

### Run now

```csharp
try
{
	await scheduler.RunNowAsync( id );   // 202 over HTTP: recorded, not yet executed
}
catch( RunInProgressException e )
{
	// 409: a run is in progress, live or lapsed. e.Run, e.Holder and e.LeaseExpiry describe it,
	// or are null when a remote response carried no readable RunInProgressResultInfo body.
}
```

- It raises `State.RunRequested`. The next poll by a worker that claims this job type runs it as `MANUAL`. The request never moves `NextRun`. The manual run's completion may: under `FIRE_ONCE` and `SKIP` it coalesces an occurrence that was already due.
- **Refused, not queued, while a run is in progress**, including a crashed run awaiting adoption. Check `ScheduleInfo.ExecutionLease is null` first, or retry after the run finishes.
- Requesting twice before a worker claims gives one run.
- **A manual run is not retried.** During a retry's backoff, though, it *is* that retry, with the same occurrence and attempt.
- On a paused schedule it is accepted, but waits for resume. It works on an exhausted schedule.
- It takes no parameters: the run uses the stored payload.

## Permissions

`Scheduler` authorizes every call through the host's `ISecurityManager`. Over HTTP the endpoints authorize nothing themselves: they require an authenticated caller with a principal (plus any policies of the host's) and translate the outcome.

**Owner rule:** when the schedule's `Owner` is `{ Type = "USER", Identifier = <the caller's identifier> }`, the caller may read, update, delete, pause, resume, run and read the history of it with no grant. Everyone else needs the operation below. Schedules owned by a principal of any other type (a group, a service) are reachable only through grants. The one exception is listing: see `LIST` below.

| `SecurityOperations` | Needed for | Checked on |
|---|---|---|
| `CREATE_ON_BEHALF` | Registering for an owner other than yourself | `{ Domain, Type = owner.Type, Identifier = owner.Identifier }` |
| `LIST` | `GetSchedulesAsync`, unless the owner filter is `{ Type = "USER", Identifier = <you> }` | `{ Domain, Type = "SCHEDULE" }` |
| `GET_INFO` | `GetScheduleAsync` | `{ Domain, Type = "SCHEDULE", Identifier = schedule }` |
| `SET_DEFINITION` | `UpdateDefinitionAsync` | the same |
| `DELETE` | `DeleteAsync` | the same |
| `PAUSE`, `RESUME` | `PauseAsync`, `ResumeAsync` | the same |
| `RUN_NOW` | `RunNowAsync` | the same |
| `GET_RUN_HISTORY` | `GetRunsAsync` | the same |

`"SCHEDULE"` is `SecurityObjectTypes.Schedule`. There is no `CREATE`: registering for yourself is always allowed. How grants map to roles is the host's choice (see `scheduling-host`).

## Errors

The same exceptions surface in-process and through `SchedulerClient`:

| Exception | When | HTTP |
|---|---|---|
| `ArgumentException` (and `ArgumentNullException`) | An invalid definition, an unregistered job type, payload validator errors, a changed `Domain`, a blank domain, a half-given owner, or a timeout over `TimerBounds.Maximum`. In-process, also a blank identifier (over HTTP, see Pitfalls) | 400 `BadRequestResultInfo { Type, RequestArgument, Message }` with `Type` `MISSING_ARGUMENT` or `INVALID_ARGUMENT`. The client rebuilds the `ArgumentException` |
| `NotAuthorized` | No grant, and not the owner | 403 |
| `UnknownScheduleException` | The schedule does not exist | 404 |
| `RunInProgressException` | Run-now while a run is in progress | 409 `RunInProgressResultInfo { Schedule, Run, Holder, LeaseExpiry }` |
| `System.Security.Authentication.AuthenticationException` | HTTP only: no or invalid credentials | 401 |

## Pitfalls

- **A partial update resets policies to the defaults.** Always start from `current.Definition with { … }`.
- **`Payload` is a string.** Serialize your object to JSON yourself. Over raw HTTP it is a JSON *string value*, not a nested object.
- **Times are UTC.** `NotBefore`, `NotAfter` and every returned `DateTime` are UTC, and a `DateTimeKind.Local` window value is misread, not converted. Show times in the user's zone only for display.
- **Durations are whole seconds.** `Interval`, `Threshold`, `Backoff` and `Timeout` with a fraction of a second are refused with `ArgumentException` (400). `Interval` must be at least 1 s and a specified `Timeout` at least 5 s (a zero `Timeout` means "use the default"), `Backoff` not negative, and `Threshold` at least the host's `MinimumMisfireThreshold` (default 15 s).
- **The payload is not secret.** Anyone with `LIST` or `GET_INFO` on the schedule reads it. Store references to secrets, never the secrets.
- **A blank or whitespace identifier is not reliably a 400 over HTTP.** Several schedule routes answer 500 today (a defect). Validate identifiers before calling.
- **Time zones are resolved on the scheduler host.** An identifier that works on your machine fails with 400 if the host's time-zone data lacks it (for example, a container image without tzdata).
- **Run-now during a run gets 409.** It is a normal outcome. Surface it ("already running") rather than retrying in a loop.
- **Run-now on a paused schedule silently waits** for resume.
- **Changing an interval schedule's window resets its phase**, because a recurrence change recomputes from now (or from `NotBefore`).
- **A `SKIP` schedule with a tight `Threshold` skips occurrences** whenever workers are busy or slow to poll. Keep the threshold well above the workers' poll interval.
- **The client's `basePath` must match the host's.** On a mismatch, calls that name a schedule report `UnknownScheduleException`, as if the schedule did not exist. `GetSchedulesAsync` and `RegisterAsync` surface the transport's `UnknownResourceException`.
- **No principal, no scheduler.** In-process calls from background code fail unless they run under an explicit principal.
- **Listing is per domain.** There is no cross-tenant query. Call `GetSchedulesAsync` once per domain the caller may see.
