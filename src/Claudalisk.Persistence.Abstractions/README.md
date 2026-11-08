# Claudalisk.Persistence.Abstractions

`IUnitOfWork` and `ConcurrencyConflictException`, without EF Core or Npgsql types. Handlers and the dispatching library reference this package, and `Claudalisk.Persistence` implements it.

## IUnitOfWork

```csharp
await unitOfWork.RunAsync(async token =>
{
    await ordering.SaveChangesAsync(token);
}, cancellationToken);
```

- `RunAsync` runs the work in one transaction. It commits when the work returns and rolls back when the work throws. The overload with a type argument returns what the work returns.
- A nested `RunAsync` joins the outer transaction. It starts none of its own, and the outer call commits or rolls back for both.
- `IsActive` is true while a unit of work runs in the scope.

The dispatcher runs every command in a unit of work, so a handler seldom calls `RunAsync` itself.

## ConcurrencyConflictException

Thrown when a write finds that someone else changed the data since it was read. The dispatcher sends the command again, up to `DispatchingOptions.ConcurrencyAttempts` times. Other code that catches it should read the data again before it retries. The web package answers 409.
