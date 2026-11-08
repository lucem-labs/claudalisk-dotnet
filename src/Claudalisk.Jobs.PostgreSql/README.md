# Claudalisk.Jobs.PostgreSql

The PostgreSQL transport for `Claudalisk.Jobs`: one `jobs` table in each jobs module's schema.

## Setup

```csharp
services.AddClaudaliskPersistence(connectionString);
services.AddClaudaliskJobs();
services.AddClaudaliskPostgreSqlJobs();
```

`AddClaudaliskPostgreSqlJobs` registers the transport as a singleton and the writer as a scoped service. It needs `AddClaudaliskPersistence`.

- The writer uses the scope's connection, so a job row is inserted in the unit of work that queues it. If the unit of work rolls back, the job is never queued. It also sends a `NOTIFY` on the module's channel.
- The transport claims jobs with `FOR UPDATE SKIP LOCKED`, so workers do not take the same row. An idle worker waits with `LISTEN` on the module's channel.

## The table

An app creates the table from a migration:

```csharp
migrationBuilder.Sql(JobsTable.CreateSql("ordering"));
```

The schema must be a lower-case identifier (letters, digits and underscores, not starting with a digit), or the helpers throw `ArgumentException`. `CreateSql` never changes. Later changes are separate helpers, each with a drop counterpart:

- `CreatePruneIndexSql` / `DropPruneIndexSql`
- `CreateRunningIndexSql` / `DropRunningIndexSql`: add it in every module schema, because the stale sweep searches running rows for old heartbeats
- `CreateClaimIndexSql` / `DropClaimIndexSql`: replaces the claim index with a partial one that matches the claim order
- `CreateDedupeIndexSql` / `DropDedupeIndexSql`

`DropDedupeIndexSql` does not restore the queued duplicates that `CreateDedupeIndexSql` canceled. The `Claudalisk.Jobs` README explains delivery, retries, deduplication and lanes.

## Limits

- Delivery is at least once.
- The package uses PostgreSQL features (advisory locks, `LISTEN`, `SKIP LOCKED`, `jsonb`) and no other database.
