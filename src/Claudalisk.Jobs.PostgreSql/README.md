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
- `RenameColumnsSql` / `RestoreColumnsSql`: renames six columns so they carry the mark of their type

`DropDedupeIndexSql` does not restore the queued duplicates that `CreateDedupeIndexSql` canceled.

### Column names

From 0.1.0-alpha.5 the transport reads and writes these names, so every jobs table needs `RenameColumnsSql` before the app runs on that version:

| `CreateSql` creates | `RenameColumnsSql` renames it to |
| --- | --- |
| `not_before` | `not_before_timestamp` |
| `created_at` | `created_timestamp` |
| `started_at` | `started_timestamp` |
| `heartbeat_at` | `heartbeat_timestamp` |
| `finished_at` | `finished_timestamp` |
| `cancel_requested` | `is_cancel_requested` |

An app adds one migration for each schema that has a jobs table:

```csharp
protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RenameColumnsSql("ordering"));

protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(JobsTable.RestoreColumnsSql("ordering"));
```

- A rename keeps the rows, and PostgreSQL updates the indexes to follow the columns.
- A new app calls `RenameColumnsSql` right after `CreateSql`, in the same migration.
- The index helpers work on either set of names, so they may come before or after the rename.
- The names of `JobRecord` and of the JSON the jobs router returns do not change. The `Claudalisk.Jobs` README explains delivery, retries, deduplication and lanes.

## Limits

- Delivery is at least once.
- The package uses PostgreSQL features (advisory locks, `LISTEN`, `SKIP LOCKED`, `jsonb`) and no other database.
