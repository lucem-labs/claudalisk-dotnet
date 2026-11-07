# Persistence migration runbooks

These runbooks cover the migration work that `Claudalisk.Persistence` supports beyond ordinary migrations:

- resetting a module's migrations to one baseline;
- retiring the history a baseline replaced;
- moving a module's tables out of a shared context;
- renaming a module.

Each one is an exception to the rule that applied migrations never change (CLA-0010), or a large data move (CLA-0012). Get the owner's approval in the issue before starting.

The examples use the modules `ordering` and `billing`, and an old shared schema `public`.

## How the library migrates

`DatabaseInitializer.MigrateAsync` runs three steps in order:

1. **History moves:** every `AddMovedMigrationHistory(from, to)` registration moves a module's migration history table from one schema to another.
2. **Baselines:** every `AddMigrationBaseline(schema, migration, replaces)` registration records a baseline in place of the migrations it replaces.
3. **Migrations:** each registered context migrates, in registration order.

Each step changes nothing when it has no registrations. Steps 1 and 2 each run in their own unit of work, so a refusal undoes everything its own step did. A step that already finished stays committed: when a baseline is refused after a history move, the move stays.

## Migration histories and naming conventions

`UseModuleSchema` keeps each module's history in its own schema, as `<schema>."__EFMigrationsHistory"`. Its columns follow the context's naming convention:

| context | history columns |
|---|---|
| a module context, through `UseModuleSchema` (snake_case) | `migration_id`, `product_version` |
| a context without a naming convention, such as an old shared context | `MigrationId`, `ProductVersion` |

Code that reads or writes a history takes the table and column names from EF Core, never from hard-coded names:

- resolve `IHistoryRepository` from a context configured like the one that wrote the history;
- use its `ExistsAsync`, `GetAppliedMigrationsAsync`, `GetInsertScript` and `GetDeleteScript`.

`MigrationBaselines` does this. It reads every history before it writes any, outside the transaction. Npgsql's `ExistsAsync` detects a missing table by catching a failed query, and inside a transaction that failure would abort it. So call `DatabaseInitializer.MigrateAsync` outside any unit of work.

`MigrationBaselines` reads histories through `UseModuleSchema`, so it handles only module histories, with snake_case columns. A baseline replaces a module's own migrations, never a shared context's.

A script that reads histories without EF Core, such as a backup manifest, must handle both column sets. It can also ask the Host to list the migrations instead.

## Resetting a module's migrations to one baseline

Use this when a module's history is a long chain that only moved or reshaped old tables, and the owner has approved replacing it with one generated migration.

### 1. Prepare the environments

Every database that will adopt the baseline must hold every migration the baseline replaces:

- uat and dev run the last commit before the baseline;
- a backup that will be restored later is first restored and migrated by that commit;
- anything older is refused by the baseline step, and the refusal says so.

### 2. Generate the baseline

1. Delete the module's migrations and model snapshot.
2. Generate one migration from the model, such as `InitialOrdering`.
3. Add by hand everything the model does not describe, which the old migrations created in raw SQL. Compare against the old migrations, not the model:
   - library tables, such as the jobs table (`JobsTable.CreateSql(schema)` from `Claudalisk.Jobs.PostgreSql`);
   - expression indexes, such as a unique index on `lower(name)`, and partial indexes;
   - default rows, inserted idempotently (CLA-0009).
4. Get the removed migrations past the migration guards. The CI check and the uat deploy refuse a commit that drops a migration tracked on `origin/main` or applied to uat (CLA-0010, CLA-0030). The deploy refuses before it migrates, so it refuses before the baseline step can run. CLA-0010's exceptions are lines in `approved-migration-edits.txt`, which only the owner edits, read from `origin/main`. CLA-0010 does not yet define a line for a removed migration, which has no file to hash, and the deploy does not read one yet. Settle both with the owner before the pull request (lucem-labs/claudalisk#85).

### 3. Register the baseline

In the module's `ConfigureServices`, next to `AddModuleDbContext`:

```csharp
services.AddMigrationBaseline(
    OrderingDbContext.Schema,
    "20270110120000_InitialOrdering",
    ["20260901100000_CreateOrders", "20260915090000_OrderVersions", "20261002140000_MoveOrdersToOrdering"]);
```

`replaces` lists exactly the migrations the baseline replaces. On each database, the step then decides per schema:

| the schema's history | what happens |
|---|---|
| holds exactly the replaced migrations | the baseline is recorded in their place; no table or row changes |
| already records the baseline | nothing |
| does not exist or is empty, and the schema has no tables | nothing; the migrate step applies the baseline |
| misses a replaced migration, or holds one that is not listed | refused, with the missing and unknown ids |
| does not exist or is empty, but the schema has tables | refused |

Every schema that adopts a baseline does so in one transaction, so a refusal in one schema changes no other.

### 4. Test it

- **Fresh database:** the app's fresh-database test migrates up, down and up again with the baselines (CLA-0011).
- **Hand-added parts:** a test checks that a fresh database has each library table, expression or partial index and default row from step 2. Tests on a fresh database pass without them, so only this test keeps them.
- **Adoption:** a test builds a database at the last layout before the baseline, inserts rows, migrates, and checks that the history records the baseline, the schema matches a fresh database's and every row is kept.

### 5. Compare schemas before the pull request

Fresh-database tests cannot show what the old migrations created and the baseline forgot. Compare schemas:

1. Migrate a fresh, empty database with the baselines.
2. Dump its schema with `pg_dump --schema-only`.
3. Dump the schema of uat's data, restored on dev and migrated by the last commit before the baseline.
4. Compare the two dumps. The only allowed differences are:
   - column order;
   - how PostgreSQL prints an equivalent `CHECK` constraint;
   - the retired history table, until it is dropped.

Put the result in the pull request.

### 6. Accept it on dev

1. Refresh dev from uat.
2. Dump dev's schema.
3. Run the migrate step of the baseline commit.
4. Dump dev's schema again. The two dumps must be identical, and each module's history must hold only its baseline.

### 7. Retire the old history

When the baselines replace migrations that moved tables out of a shared context, the same change removes the legacy context and its migrations (see below). The legacy context's history stays behind, such as `public."__EFMigrationsHistory"`. Once the legacy context is gone, nothing reads that history. While it is still registered, its migrate step reads the history, and dropping it would replay every old migration. Do not keep code that guards the retired history, and do not add exceptions for it. Instead:

1. Wait until every environment has adopted the baseline and runs without the legacy context.
2. Add a one-off raw-SQL migration to one module, which drops the retired history:

   ```csharp
   migrationBuilder.Sql("DROP TABLE IF EXISTS public.\"__EFMigrationsHistory\";");
   ```

   Its `Down` recreates nothing: the history it drops describes migrations that no longer exist.
3. In the same pull request, delete any code that handled the retired history. The owner removes its lines from `approved-migration-edits.txt` on `main`.
4. Declare backups older than the baseline's starting layout unsupported, in the pull request and in the app's backup docs. To bring one forward, restore it and migrate it with the last commit before the baseline, and then with the current one.

The uat deploy counts a migration as missing only in a schema the commit migrates (CLA-0030), so a retired history needs no exception, not even in the deploy that drops it.

## Moving a module's tables out of a shared context

Use this when an app's tables live in one shared context, and its modules move to their own schemas and contexts one at a time (CLA-0007).

### 1. Keep the old context as a legacy context

The old context stays, with its migrations unchanged, so a fresh database still replays the old history:

- its model becomes empty, and its model snapshot follows;
- it keeps its old options, without `UseModuleSchema`, so its history stays where it was, with its old column names;
- it is registered first, so it migrates before every module context.

```csharp
public static IServiceCollection AddLegacyDatabase(this IServiceCollection services)
{
    services.AddDbContext<LegacyDbContext>((provider, options) =>
        options.UseNpgsql(provider.GetRequiredService<IScopeConnection>().Connection));
    services.AddSingleton(new ModuleDbContextRegistration(typeof(LegacyDbContext), "public"));
    return services;
}
```

The Host calls `AddPersistence`, then `AddLegacyDatabase`, then `AddModules`. The database is not a module, so no module depends on the legacy context. The legacy context only migrates. It has no `UseModuleSchema` interceptor, so it never joins a unit of work, and nothing reads or writes data through it.

### 2. Move the tables in place

In the module's new context, one migration moves its tables out of the shared schema. It renames them in place and never copies and drops:

```csharp
migrationBuilder.EnsureSchema("ordering");
migrationBuilder.Sql("ALTER TABLE public.\"Orders\" SET SCHEMA ordering;");
migrationBuilder.Sql("ALTER TABLE ordering.\"Orders\" RENAME TO orders;");
migrationBuilder.Sql("ALTER TABLE ordering.orders RENAME COLUMN \"PlacedAt\" TO placed_timestamp;");
migrationBuilder.Sql("ALTER TABLE ordering.orders RENAME CONSTRAINT \"PK_Orders\" TO pk_orders;");
migrationBuilder.Sql("ALTER INDEX ordering.\"IX_Orders_CustomerId\" RENAME TO ix_orders_customer_id;");
```

- Rename every table, column, key, check and index to the names the new context's model expects (CLA-0051).
- `Down` reverses every step exactly, in reverse order.
- A foreign key to a table in another module's schema is dropped, since nothing crosses schemas (CLA-0007).
- The module's model snapshot must match the moved tables, so the next generated migration is empty.

### 3. Test it

A test checks that the move keeps every value:

1. Create an empty database, and migrate only the legacy context, to the old layout.
2. Insert rows into every moved table, covering every column, including nulls and edge values.
3. Migrate everything.
4. Read every row from the new tables and compare every value.
5. Migrate down past the move and check the old layout and its values again.

The app's fresh-database test (CLA-0011) also still runs, through the legacy migrations and every move.

### 4. Finish

When every module has moved, the legacy context has no tables left. A later issue resets the module migrations to baselines, as described above, and removes the legacy context and its migrations.

## Renaming a module

A module's name sets its schema, and its migration history lives in that schema. So a rename cannot be done by editing the module's applied migrations (CLA-0010). It takes two parts, both in one pull request.

### 1. Move the history before migrating

Register the move in the renamed module's `ConfigureServices`, from the old schema to the new one:

```csharp
services.AddModuleDbContext<FulfilmentDbContext>(FulfilmentDbContext.Schema);
services.AddMovedMigrationHistory("shipping", FulfilmentDbContext.Schema);
```

Before the baselines and migrations run, the step:

| the database | what happens |
|---|---|
| has a history in the old schema only | creates the new schema and moves the history table into it |
| has a history in the new schema only | nothing; the move already happened |
| has neither | nothing; a fresh database migrates as usual |
| has a history in both schemas | refused, with nothing changed |

Schema names are lower-case identifiers, and the two must differ.

### 2. Move every table in a new migration

A new migration in the renamed module moves everything else and drops the old schema:

```csharp
migrationBuilder.EnsureSchema("fulfilment");
migrationBuilder.Sql("ALTER TABLE shipping.shipments SET SCHEMA fulfilment;");
migrationBuilder.Sql("ALTER TABLE shipping.jobs SET SCHEMA fulfilment;");
migrationBuilder.Sql("DROP SCHEMA shipping;");
```

- Move every table, including the jobs library's table and every other library table in the schema. Sequences owned by a column move with their table; move any other sequence, view or function too.
- `DROP SCHEMA` without `CASCADE` fails if anything was left behind.
- `Down` recreates the old schema and moves everything back. The history stays where step 1 put it, because the renamed module's context reads it there. `Down` is for the fresh-database test. To roll a deploy back to the old name, restore the backup taken before it (CLA-0031). The old code looks for its history in the old schema, finds none beside its tables, and fails.

The rename also changes the module's other names: its projects and namespaces, job links (`/api/jobs/<module>/{id}`), problem types (`/problems/<module>/<slug>`), the module named in its schedules and the OpenAPI tag. Old job links and problem types stop resolving, and the frontend's entity slice is renamed to match.

### 3. Test it

- A database at the old name, with rows and jobs, migrates to the new name and keeps every row, and the old schema is gone.
- Migrating it twice changes nothing the second time.
- The fresh-database test (CLA-0011) migrates up, down and up again.

### 4. Remove the move

Once every environment has migrated past the rename, `AddMovedMigrationHistory` has nothing left to move. Remove it in a later pull request. Backups from before the rename are then migrated by the last commit that still has it.
