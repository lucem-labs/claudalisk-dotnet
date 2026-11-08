# Claudalisk.AspNetCore

What every Host needs: one Problem Details handler, conditional writes, HAL members, the health endpoint and module tags in the OpenAPI document.

## Problem types

`AddClaudaliskWeb` registers one exception handler. Every error is an `application/problem+json` body.

| Exception | Status | Type |
| --- | --- | --- |
| `DomainException` | 404, 403 or 409 by its kind | `/problems/{module}/{slug}` (`/problems/{slug}` outside a module) |
| `MessageValidationException` | 400 | none; a validation problem with `errors` |
| `StaleVersionException` | 412 | `/problems/precondition-failed` |
| `ConcurrencyConflictException` | 409 | `/problems/concurrency-conflict` |
| `PreconditionRequiredException` | 428 | `/problems/precondition-required` |
| `DependencyUnavailableException` | 503 with `Retry-After` | `/problems/dependency-unavailable` |
| `BadHttpRequestException` | its own status | none |
| anything else | 500 | none |

## Stale writes and lost races

- A write that carries `If-Match` loads the aggregate and calls `AggregateRoot.RequireVersion` with that version. A mismatch throws `StaleVersionException` and the response is 412. The client reads the resource again. The dispatcher never retries it.
- A `ConcurrencyConflictException` that still reaches the Host has lost the race on every attempt (`DispatchingOptions.ConcurrencyAttempts`). The response is 409. The client may send the same request again.

## Retry-After

A `DependencyUnavailableException` answers with `Retry-After` in whole seconds, rounded up: the exception's `RetryAfter`, else `ClaudaliskWebOptions.DefaultRetryAfter` (30 seconds, must be positive). The health endpoint's 503 uses the same type and header.

## Logging

A 503 is logged at Warning with the exception. A 500 is logged at Error. Other statuses are not logged by the handler.

## OpenAPI

Every operation lists the 409, 412, 428 and 503 problem responses above as `application/problem+json` with the ProblemDetails schema, unless the endpoint already declares a response for that status.

## If-Match

`Request.RequiredIfMatchVersion()` reads one strong version tag such as `"7"`. A missing header throws `PreconditionRequiredException` (428). A malformed one throws a `BadHttpRequestException` (400) whose message does not repeat the value that was sent.
