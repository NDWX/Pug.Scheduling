# The schedule management HTTP API

What `MapSchedulingEndpoints()` serves, for callers that don't use `SchedulerClient`. Routes are relative to the API's base path: `schedules` by default, or the host's `SchedulingEndpointOptions.BasePath`, plus any prefix the host mounts it under.

Every route requires an authenticated caller (401 otherwise) whom the host maps to a principal (403 otherwise), plus the authorization policies the host configured for the API (its default policy when it configured none). A refused policy is 403. Beyond that gate, every grant decision is the scheduler's (see "Permissions" in the skill).

| Route | Input | Success | Failure |
|---|---|---|---|
| `GET {base}?domain=&ownerType=&ownerIdentifier=&jobType=` | `domain` is required. Owner as a pair, both or neither | 200 `ScheduleInfo[]` | 400, 403 |
| `POST {base}?ownerType=&ownerIdentifier=` | Body: `ScheduleDefinition`. Owner pair optional (on-behalf) | 201, body: the new identifier as a JSON string | 400, 403 |
| `GET {base}/{identifier}` | | 200 `ScheduleInfo` | 403, 404 |
| `PUT {base}/{identifier}` | Body: `ScheduleDefinition` (the whole definition) | 200 | 400, 403, 404 |
| `DELETE {base}/{identifier}` | | 200 | 403, 404 |
| `POST {base}/{identifier}/pause` | No body needed | 200 | 403, 404 |
| `POST {base}/{identifier}/resume` | No body needed | 200 | 403, 404 |
| `POST {base}/{identifier}/run` | No body needed | **202** (recorded, not yet run) | 403, 404, **409** `RunInProgressResultInfo` |
| `GET {base}/{identifier}/runs?from=&to=` | `from`, `to`: optional UTC instants with a `Z` (`2026-11-15T03:00:00Z`), inclusive, on `Started` | 200 `ScheduleRun[]`, most recent first | 403, 404 |

## Bodies

JSON uses ASP.NET Core's web defaults: camelCase names, and case-insensitive on input. Constants such as `FIRE_ONCE` are plain strings. A `TimeSpan` is a string in `[d.]hh:mm:ss` form, and a `DateTime` is ISO 8601 in UTC. **Send timestamps in UTC with a `Z` suffix.** The scheduler treats any `DateTime` as UTC without converting it, so an unsuffixed value is taken as UTC, and a value with an offset is not reliably converted.

A `ScheduleDefinition`:

```json
{
  "domain": "tenant-42",
  "name": "Weekly sales report",
  "jobType": "report",
  "payload": "{\"report\":\"sales\",\"format\":\"pdf\"}",
  "recurrence": { "cronExpression": "0 7 * * MON", "timeZone": "Australia/Melbourne" },
  "misfirePolicy": { "handling": "FIRE_ONCE", "threshold": "00:30:00" },
  "retryPolicy": { "maximumAttempts": 3, "backoff": "00:05:00" },
  "timeout": "00:15:00"
}
```

- `payload` is a JSON **string** holding the job's JSON document, not a nested object.
- For an interval, send `"interval": "01:00:00"` instead of `cronExpression`. Add `"notBefore"` / `"notAfter"` (UTC, with a `Z`) for a window.
- Omit `misfirePolicy`, `retryPolicy` or `timeout` to get the host's defaults. On `PUT` this **resets** them.

A 400 carries `BadRequestResultInfo`:

```json
{ "type": "INVALID_ARGUMENT", "requestArgument": "recurrence", "message": "Unknown or invalid time zone 'Mars/Olympus'. (Parameter 'recurrence')" }
```

`type` is `MISSING_ARGUMENT` (a required query, route or body value was absent) or `INVALID_ARGUMENT` (the scheduler rejected a value). `requestArgument` names the parameter.

A 409 from `run` carries `RunInProgressResultInfo`:

```json
{ "schedule": "s-1", "run": "r-9", "holder": "jobs-host-a/2", "leaseExpiry": "2026-11-15T03:04:05Z" }
```

`holder` is the worker holding the run, as `claimant/number`. A `leaseExpiry` in the past means the run's worker stopped heartbeating, and the run is waiting for another worker to adopt it.

403 and 404 have no body.
