# Claudalisk.Persistence

EF Core on PostgreSQL for module contexts: one connection and one transaction per scope, the unit of work and the migration runner.

## Setup

```csharp
services.AddClaudaliskPersistence(connectionString);
services.AddModuleDbContext<OrderingDbContext>("ordering");
```

- `AddClaudaliskPersistence` registers the data source, the scope's connection, `IUnitOfWork` and `DatabaseInitializer`. A host has one database, so calling it again with another connection string throws.
- `AddModuleDbContext<T>(schema)` registers a context in its own schema, with its migration history in `<schema>."__EFMigrationsHistory"` and snake_case names. Each context needs its own schema. `UseClaudaliskModuleSchema` does the same inside an `AddDbContext` call you write yourself.

## One connection and one transaction

Every module context in a scope uses the scope's connection, and `IUnitOfWork.RunAsync` begins one transaction on it. Each context takes part in that transaction. This is EF Core's documented pattern for [sharing a connection and a transaction](https://learn.microsoft.com/en-us/ef/core/saving/transactions#share-connection-and-transaction).

- A nested `RunAsync` joins the outer transaction. It commits or rolls back with the outer call.
- A failed concurrency check surfaces as `ConcurrencyConflictException`.

## What the guard refuses

A module context is refused, when its options are built or validated, if it:

- is pooled with `AddDbContextPool`. Each context needs its own scope's connection, and a pool reuses context instances across scopes. See the EF Core notes on [DbContext pooling](https://learn.microsoft.com/en-us/ef/core/performance/advanced-performance-topics#dbcontext-pooling).
- sets an execution strategy, which is what `EnableRetryOnFailure` does. A retrying strategy does not work with a transaction that the unit of work began. See [connection resiliency](https://learn.microsoft.com/en-us/ef/core/miscellaneous/connection-resiliency). Retry the whole `RunAsync` call instead.

Both refusals throw `InvalidOperationException` with a message that says what to do. `UseClaudaliskModuleSchema` also throws when `AddClaudaliskPersistence` was not called.

## Migrations

`DatabaseInitializer.MigrateAsync` moves migration histories, adopts baselines, then migrates each context in registration order. `GetPendingMigrationsAsync` lists what is pending as `schema: migration`.

Call `MigrateAsync` outside a unit of work. Migrations manage their own transactions, so inside `RunAsync` it throws `InvalidOperationException`.

`AddClaudaliskMigrationBaseline(schema, migration, replaces)` and `AddClaudaliskMovedMigrationHistory(from, to)` support resetting a module's migrations and moving tables out of a shared context. Both change history that was already applied, so follow the [migration runbooks](https://github.com/lucem-labs/claudalisk-dotnet/blob/main/docs/persistence-migrations.md) and get the owner's approval first.
