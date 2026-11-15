---
name: scheduling-concepts
description: Use when designing with, explaining or debugging Pug.Scheduling (the tenant-aware, database-backed job scheduler), i.e. understanding schedules, runs, owners and execution leases, the roles of IScheduler, IExecutionCoordinator, workers and job handlers, the claim, execute, heartbeat and report lifecycle, at-least-once execution and the (Schedule, ScheduledFor) idempotency key, adoption of orphaned runs, triggers (SCHEDULE, MISFIRE, RETRY, MANUAL), misfire policies (FIRE_ONCE, SKIP, RUN_ALL), retry backoff, run-now, the scheduling clock, or deciding whether Pug.Scheduling can do something at all. Start here, then load the sibling skill for the task.
---

# Pug.Scheduling concepts

Pug.Scheduling is a library. It stores **schedules** (a recurrence, a job type and an opaque payload, owned by a principal, in a tenant `Domain`) in one database, works out when each is due, and hands due occurrences to **workers**, which run them through **job handlers** and report back. It never executes anything itself, and workers never touch the database.

Everything below is enforced by the library. The sibling skills say how to wire and call it.

## The model

**`ScheduleInfo`** (`Pug.Scheduling.Models`), one row per schedule:

| Section | Holds | Who writes it |
|---|---|---|
| `Identifier` | From the host's `IdentifierGenerator` | Registration |
| `Definition` (`ScheduleDefinition`) | `Domain`, `Name`, `JobType`, `Payload` (a JSON string, opaque to the scheduler), `Recurrence`, `MisfirePolicy`, `RetryPolicy`, `Timeout` | The API caller (register, update) |
| `Owner` (`Pug.Effable.Reference { Type, Identifier }`) | The principal the job runs on behalf of. Not part of the definition: set at registration, never changes | Registration |
| `State` (`ScheduleState`) | `Enabled`, `Paused`, `NextRun`, `RunRequested`, `Attempt`, `Occurrence`, `LastRun` | The scheduler and coordinator only |
| `ExecutionLease` | `Run`, `ClaimedBy` (`"claimant/number"`), `Expiry`. Present only while a run is in progress, including a lapsed one awaiting adoption | The coordinator only |
| `Registration`, `LastUpdate` | `ActionContext<Reference>` audit stamps | Register, update |

- `State.NextRun` is the next occurrence, or the time of the pending retry while `Attempt > 0`. It is null when the recurrence is exhausted (past `NotAfter`).
- `State.Occurrence` is the occurrence being retried, set only while `Attempt > 0`.
- `State.LastRun` (`RunSummary { Started, Finished, Result }`) is the last finished or skipped run.
- `Domain` and `Owner` never change after registration.

**`ScheduleRun`**, one row per execution attempt (the audit trail):

| Field | Meaning |
|---|---|
| `Identifier`, `Schedule`, `Domain` | The run, its schedule, and the schedule's domain |
| `ScheduledFor` | The occurrence this run executes (the planned fire time, UTC), not when it started |
| `Attempt` | Zero-based retry attempt of that occurrence |
| `Trigger` | `RunTriggers`: `SCHEDULE`, `MISFIRE`, `RETRY`, `MANUAL` |
| `Started`, `Finished` | `Finished` is null while the run is in progress |
| `Result` | `RunResults`: `SUCCEEDED`, `FAILED`, `TIMED_OUT`, `SKIPPED`. The worker's classification; null while in progress |
| `Outcome` | The handler's `JobOutcome { Successful, Details, Error }`. Null in progress and for `SKIPPED` runs |
| `ClaimedBy` | The last worker to claim it (audit only) |

`(Schedule, ScheduledFor, Attempt)` is unique. Run history is deleted only with its schedule.

## Roles

| Role | Type (package) | Does |
|---|---|---|
| Management | `IScheduler` (`Pug.Scheduling.Abstractions`), implemented by `Scheduler` (`Pug.Scheduling`) and `SchedulerClient` (`Pug.Scheduling.Client`) | Register, read, update, delete, pause, resume, run-now, run history. Configuration only: it never executes |
| Execution coordination | `IExecutionCoordinator` (`Pug.Scheduling.Abstractions`), implemented by `ExecutionCoordinator` and `ExecutionCoordinatorClient` | Claim, renew, abandon, report. **The workers' only surface.** Due-ness, misfire, retry and next-occurrence rules all live behind it |
| Worker | `SchedulerWorker` (`Pug.Scheduling.Worker`), a hosted service | Polls the coordinator, runs one attended job at a time, heartbeats, reports |
| Job handler | `IScheduledJobHandler` + `IScheduledJobHandlerFactory` (`Pug.Scheduling.JobHandler.Abstractions`) | Your code: executes one run of a job type and returns a `JobOutcome` |

Both facades are the same contract in-process and over HTTP, exceptions included. A worker cannot tell whether its coordinator is local or remote.

## The run lifecycle

1. **Poll.** An idle worker calls `ClaimDueJobAsync( claimant, worker, jobType )` every `PollInterval` (10 s by default), and again at once after finishing a job. `jobType` null claims any type.
2. **Claim.** The coordinator reads a window of due candidates (`ClaimCandidates`, default 10), then, per candidate, in one transaction: locks the schedule row, classifies it at the clock's current time, and acts on it. It returns the first job it can hand out, or null.
3. **Execute.** The worker creates a handler through the job type's factory and calls `ExecuteAsync( context, timeout, cancellationToken )`.
4. **Heartbeat.** Every `HeartbeatInterval` (30 s by default) the worker calls `RenewClaimAsync`, which pushes the lease to now + `LeaseDuration` (2 min by default). `false` means the claim is lost: the worker cancels the handler and never reports.
5. **Report.** `ReportCompletionAsync( run, claimant, worker, result, outcome )` finishes the run, clears the lease and moves the schedule on (next occurrence, or a retry), all in one transaction.
6. **Dispose.** The worker disposes the handler, whatever happened.

What a claim does with a locked schedule, in precedence order:

| The schedule | Claim action |
|---|---|
| Disabled or paused | Nothing, even if a run is in progress or a run-now is pending |
| Has a run in progress whose lease lapsed, or is held by this same worker (`claimant/number`) | **Adopt** it: same run row, same `ScheduledFor`, `Attempt` and `Trigger`. The handler runs again from the start |
| Has a live lease held by another worker | Nothing |
| `RunRequested` | Start a `MANUAL` run |
| `NextRun` not reached yet, or null | Nothing |
| `Attempt > 0` | Start a `RETRY` run of `State.Occurrence`, however late |
| Overdue by no more than `MisfirePolicy.Threshold` | Start a `SCHEDULE` run |
| Overdue by more | `FIRE_ONCE`/`RUN_ALL`: start a `MISFIRE` run. `SKIP`: record a `SKIPPED` run and advance, without executing |

## Guarantees

- **At-least-once, never exactly-once.** An occurrence runs again when a worker crashes, or when renewals fail for longer than the lease: whichever worker next claims after the lease lapses adopts the unfinished run. It also runs again when a completion report fails. The worker logs the failure and polls again at once, and because it is still the run's holder, it adopts its own run immediately, without waiting for the lease. A report that keeps failing (for example, a remote worker without the `REPORT_COMPLETION` grant) therefore becomes a tight re-execution loop that never uses up an attempt.
- **Idempotency key: `(Schedule, ScheduledFor)`.** Every re-execution of an occurrence (an adoption, a retry) carries the same `ScheduledFor`. Handlers key external side effects on it. An adoption also keeps `Run` and `Attempt`. A retry gets a new `Run` and a higher `Attempt`.
- **No duplicate occurrences.** Adoption takes precedence over every other rule, so an occurrence in progress is never recorded twice.
- **One run per schedule at a time.** A schedule with a lease is never started again. Occurrences that come due during a long run are coalesced or run afterwards (see misfire policies), never in parallel. The one exception is a detached handler (below).
- **One attended job per worker.** A handler that ignores cancellation for longer than `CancellationGrace` (10 s by default) after its timeout, claim loss or host shutdown is **detached**: the worker reports it `TIMED_OUT` (on a timeout) or abandons it, and moves on. A detached handler can overlap its own retry or a re-execution elsewhere.
- **Restart takeover.** A restarted worker host with the same claimant and worker numbers adopts its own unfinished runs at once, without waiting for their leases.
- **Graceful shutdown releases the claim** when the handler has stopped, so a peer adopts the run immediately rather than after the lease (DECISIONS D27).
- **Every state change is lock, decide, write** in one transaction on the schedule row (D23). Concurrent coordinators and management calls cannot interleave inside a transition.

## Triggers

| `RunTriggers` | Started because | `ScheduledFor` |
|---|---|---|
| `SCHEDULE` | The occurrence came due, within the misfire threshold | The occurrence |
| `MISFIRE` | The occurrence is overdue beyond the threshold (also the trigger of a `SKIPPED` run) | The overdue occurrence. Under `FIRE_ONCE` this is the first missed one |
| `RETRY` | A failed or timed-out run has attempts left | The original occurrence |
| `MANUAL` | Run-now | The claim time, or the retried occurrence when claimed during a retry's backoff |

## Misfire policies

An occurrence is **misfired** when the claim finds it overdue by more than `MisfirePolicy.Threshold` (default 5 min), for instance because no worker ran, the schedule was paused, or every worker was busy.

| `MisfireHandlings` | Runs the misfired occurrence | Then the next occurrence is |
|---|---|---|
| `FIRE_ONCE` (default) | Once, as `MISFIRE` | The first slot after completion. Every missed slot is coalesced into that one run |
| `SKIP` | Never: a `SKIPPED` run is recorded | The first slot after the skip |
| `RUN_ALL` | Yes, and every other missed slot after it, one run each, in order | One slot after the executed occurrence, so the backlog is worked through |

Example, hourly interval, workers down from 09:00 to 12:30: `FIRE_ONCE` runs 09:00 once and continues at 13:00. `SKIP` records 09:00 as skipped and continues at 13:00. `RUN_ALL` runs 09:00, 10:00, 11:00 and 12:00 in turn, then 13:00.

The same rule applies to slots that elapse while a run executes: `FIRE_ONCE` and `SKIP` drop them, `RUN_ALL` runs them afterwards. An interval keeps its grid: occurrences stay on `anchor + k × Interval`, never drifting to "completion time + Interval".

## Retry and backoff

- `RetryPolicy.MaximumAttempts` counts the first attempt (default 3; 1 disables retries). `FAILED` and `TIMED_OUT` both retry.
- Retry *n* (`Attempt` = *n*) is scheduled `Backoff × 2^(n−1)` after the failed run completed: with the default `Backoff` of 1 min, retries come 1 min, then 2 min, after each failure.
- A retry ignores the misfire threshold. It runs as `RETRY` however late it is claimed.
- When attempts run out, the occurrence ends as a failure and the schedule advances as it would after a success.
- A pause during backoff holds the retry until resume.

## Run-now

`RunNowAsync` only raises the `RunRequested` marker. A worker claims it on its next poll as `MANUAL` (DECISIONS D10).

- The request never moves `NextRun`. The manual run's completion may, per the policy below.
- It is **refused with `RunInProgressException`** (409 over HTTP) while a run is in progress, live or lapsed.
- A manual run is **not retried** if it fails.
- During a retry's backoff, it accelerates that retry instead: same occurrence, same attempt, and its failure follows the retry policy.
- On completion, `FIRE_ONCE` and `SKIP` coalesce an occurrence that was already due when the manual run started. `RUN_ALL` keeps it.
- It works on an exhausted schedule. On a paused schedule it is accepted but waits until resume.

## The clock

Every time-based decision (due-ness, misfire, lease expiry, retry and next-occurrence times, run and audit timestamps) reads one clock, `ISchedulingClock`, inside the deciding transaction:

- `DatabaseClock` (the default) uses the database's time, so several scheduler hosts on one Postgres store agree, whatever their own clock skew. On SQLite this is the host's clock, so a SQLite store is single-host.
- `TimeProviderClock( TimeProvider )` is for tests and single-host deployments. With it, several hosts' clock skew must stay well under `LeaseDuration`.
- Registering a `TimeProvider` alone does not change the facades' clock. Workers use a `TimeProvider` only to stamp `WorkerInfo.LastPoll`. Their poll, heartbeat, timeout and grace waits are real timers.

All times are UTC (`DateTimeKind.Utc` from both stores). Cron is evaluated in the recurrence's IANA time zone.

## What's possible, and what's not

| Possible | Not possible |
|---|---|
| Cron (five fields, in an IANA time zone) or a fixed interval, inside an optional `NotBefore`/`NotAfter` window | A one-off schedule. Use a window holding one occurrence, or run-now |
| Per-schedule misfire policy, retry policy and timeout (up to about 49.7 days) | Seconds in cron, or sub-second durations (durations are stored in whole seconds, truncated; issue #33) |
| Run-now, pause and resume | Passing different parameters to one run, or cancelling a running job through the API |
| Changing the definition, including job type and payload | Changing `Domain` or `Owner` |
| Tenant scoping by `Domain`, owner principals, per-job-type payload validation | Listing across domains |
| Many scheduler hosts on one Postgres store; workers in-process or remote over HTTP | Exactly-once execution, job chaining, priorities |
| Worker groups dedicated to one job type | Two runs of one schedule at once, except a detached handler |
| Run history per schedule | Disabling a schedule other than by pausing (`Enabled` is always true), or pruning run history short of deleting the schedule |

## Pitfalls

- **Handlers must be idempotent on `(Schedule, ScheduledFor)`.** At-least-once is the contract, and a detached handler can overlap its own retry. See `scheduling-job-handler`.
- **A `SKIP` threshold shorter than the claim latency skips live occurrences.** Misfire is judged at claim time. When workers are saturated or polls are slow, a tight threshold turns "late" into "skipped". Keep `Threshold` well above `PollInterval` plus the longest expected queueing.
- **A schedule never overlaps itself.** A run longer than the interval coalesces (`FIRE_ONCE`, `SKIP`) or queues (`RUN_ALL`) the slots it covers. Size `Timeout` and the interval accordingly.
- **Durations are stored in whole seconds, truncated (issue #33).** This applies to `Interval`, `Threshold`, `Backoff` and `Timeout`, and validation runs before the truncation. A sub-second interval is stored as zero, and the next-occurrence calculation then divides by zero at completion, so the run can never complete: it loops through re-execution as above. A stored zero interval also makes every later read-modify-write update fail validation until `Interval` is corrected. A zero timeout times out every run at once. A zero `SKIP` threshold skips nearly every occurrence. Use whole seconds.
- **Alert on the worker's "failed to report completion" log.** A completion report that keeps failing re-executes the run endlessly (see Guarantees).
- **Run-now during a run is refused, not queued.** Check `ScheduleInfo.ExecutionLease` first, or handle the 409.
- **A paused schedule holds everything:** due occurrences, a pending run-now, a pending retry, even adoption of a crashed run. On resume, elapsed occurrences go through the misfire policy.
- **Deleting a schedule mid-run cancels the handler** at the worker's next heartbeat (the renewal is refused, answering false, which is a lost claim). Its completion is recorded nowhere.
- **`LeaseDuration` must comfortably exceed `HeartbeatInterval`**, plus any forward step of the database clock. Otherwise live runs are adopted and executed twice.
- **Two worker processes on one machine need distinct claimants.** The claimant defaults to the machine name, and identical `claimant/number` holders take each other's runs as their own.
- **A worker group with no `JobType` claims every job type.** If its host lacks the handler, each such run fails with "No handler registered" and burns an attempt.
- **Workers never reach the database.** Code that wants schedule state from a worker or handler goes through `IScheduler` like any other client, with a principal.

## Which skill next

| Task | Skill |
|---|---|
| Wiring `AddScheduling`, the SQLite or Postgres store and its migration, `ISecurityManager` and grants, endpoints, base paths and policies, the clock, job types and payload validators | `scheduling-host` |
| Running workers in-process or remotely: `AddSchedulingWorkers`, groups, claimants and numbering, `ExecutionCoordinatorClient`, coordinator grants, `WorkerOptions`, worker management endpoints | `scheduling-workers` |
| Writing a job handler or factory: context, timeout, cancellation, outcomes, idempotency, DI scope, disposal, detach | `scheduling-job-handler` |
| Creating and managing schedules through `IScheduler` or `SchedulerClient`: recurrence, policies, run-now, runs, errors, permissions | `scheduling-api` |

The D-numbers cited here refer to `docs/DECISIONS.md` in the Pug.Scheduling repository (https://github.com/NDWX/Pug.Scheduling).
