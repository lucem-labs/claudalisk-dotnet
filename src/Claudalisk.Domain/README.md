# Claudalisk.Domain

Domain building blocks: the aggregate base, domain events, `DomainException` and `DependencyUnavailableException`. The package has no dependencies.

## Aggregates and versions

An aggregate derives from `AggregateRoot`. It starts at `Version` 1 (`AggregateRoot.InitialVersion`). The constructor that takes a version, for loading a stored aggregate, throws `ArgumentOutOfRangeException` below 1.

- `IncrementVersion()` adds one. Call it when the aggregate changes.
- `RequireVersion(expectedVersion)` throws `StaleVersionException` when the aggregate is at another version. Use it to check the version a client sent, for example from an `If-Match` header.

`StaleVersionException` carries `ExpectedVersion` and `ActualVersion`. The web package maps it to 412, and the jobs library fails a job with it without retry.

## Domain events

An event is a class that implements the marker interface `IDomainEvent`. An aggregate records one with the protected `Record` method. `DomainEvents` shows the recorded events, and `TakeDomainEvents()` returns them and clears the list, so a caller publishes each event once.

## Domain errors

Derive from `DomainException` for a rule that a request broke. The constructor takes:

- `slug`: lower-case words joined by hyphens, such as `already-shipped`. It becomes the end of the problem type.
- `title`: the message, which cannot be blank.
- `kind`: `DomainErrorKind.Conflict`, `NotFound` or `Forbidden`, which the web package maps to 409, 404 and 403.
- `extensions`: optional members for the response body.
- `innerException`: optional.

```csharp
public sealed class AlreadyShippedException()
    : DomainException("already-shipped", "The order has already shipped.", DomainErrorKind.Conflict);
```

The constructor throws `ArgumentException` for a bad slug, and for an extension that is given twice (ignoring case) or would replace a Problem Details member: `type`, `title`, `status`, `detail`, `instance`, `errors` and `traceId`. `Extensions` is a read-only copy.

## Unavailable dependencies

`DependencyUnavailableException` says that something the request needs is down. It is not a domain rule. `RetryAfter` is an optional wait and cannot be negative. The web package answers 503 with a `Retry-After` header, and the jobs library retries the job, waiting at least `RetryAfter`.
