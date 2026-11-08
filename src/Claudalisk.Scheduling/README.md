# Claudalisk.Scheduling

Cron schedules from configuration. Each fire is queued as a job in the scheduler's own jobs module, and the jobs library sends the command through the dispatcher.

## Setup

```csharp
services.AddPersistence(connectionString);
services.AddModules(configuration, new CatalogModule());
services.AddDispatching();
services.AddJobs();
services.AddPostgreSqlJobs();
services.AddScheduling(configuration);
```

`AddScheduling` needs `AddPersistence`, `AddDispatching`, `AddJobs` and a jobs transport such as `AddPostgreSqlJobs`. A hosted check fails startup naming `AddJobs` and the transport when they are missing. Calling `AddScheduling` twice is harmless: the second call returns before it binds the options again.

A module marks a command as schedulable:

```csharp
services.AddSchedulableCommand<RefreshCatalogCommand>("Catalog", "refresh-catalog");
```

The module must be in the `ModuleCatalog` (compared as the kebab-case segment, as the jobs check does); startup fails naming the command, the module and the known modules otherwise. The check does nothing when no `AddModules` call registered a catalog.

## Configuration

```json
{
  "Scheduling": {
    "Enabled": true,
    "TickInterval": "00:00:30",
    "Schedules": [
      { "Name": "nightly-refresh", "Cron": "30 2 * * *", "TimeZone": "Europe/Warsaw", "Module": "Catalog", "Command": "refresh-catalog", "Arguments": "{\"source\":\"nightly\"}" }
    ]
  }
}
```

- `Enabled` defaults to `false`. A disabled scheduler fires nothing, whatever the clock does.
- `TickInterval` must be positive. Every tick the scheduler looks at each schedule.
- `Cron` is the standard five-field format (Cronos). `TimeZone` is an IANA or system id and defaults to `UTC`.
- `Arguments` is the JSON the command is deserialized from.

The schedules are parsed and validated with `ValidateOnStart`, whether or not the scheduler is enabled. Every problem is named in one message: a bad cron expression, an unknown time zone, an unknown command, a command of another module, arguments that do not fit the command, a duplicate or missing name.

## Time zones and DST

The next occurrence is computed by Cronos in the schedule's time zone. When the clocks spring forward, an occurrence in the skipped hour runs at the first valid time after the gap. When they fall back, an occurrence in the repeated hour runs once, at its first pass. Occurrences are stored in UTC.

## Claiming and catch-up

Each schedule has a row in `scheduling.schedules` with `last_fired_timestamp`. A tick locks the row (`FOR UPDATE`), so several instances can run and only one claims an occurrence.

- A schedule seen for the first time records the current time and waits for its next occurrence. It never catches up on the past.
- After downtime, all missed occurrences become one run, for the latest occurrence that is due. The latest occurrence is found without walking every missed one: the search window before `now` starts at one minute and doubles until it holds an occurrence, then walks forward to the last one at or before `now`.
- Rows of schedules that are no longer configured are deleted on the first tick of each process. A schedule added again later starts fresh. Pruning only runs in an enabled scheduler, and instances with different schedule lists would delete each other's rows, so roll out one configuration.

## The outbox and delivery

The scheduler has a jobs module named `Scheduling` with schema `scheduling`. It is library-owned: the module-name check of `AddModuleJobs` skips it, and `IJobQueue.EnqueueAsync` never resolves a command to it, so an app's own jobs of the same command type never land in the scheduler's table. Each schedulable command is a command job kind of that module, named after the schedulable command.

A fire is one unit of work: it updates the claim row and inserts the job through `IJobWriter.InsertAsync`, with the dedupe key `<schedule>:<fire time>`, the fire time in UTC in ISO 8601 `O` format. If the insert fails, the claim rolls back and the occurrence fires again on the next tick. One schedule failing does not stop the tick: the error is logged and the other schedules go on.

The jobs workers of the module (default lane, one worker; change it with `AddModuleJobs("Scheduling", "scheduling", jobs => jobs.Lane("default", n))`) send the command through the dispatcher with the retries of the jobs library. Delivery is at least once: handlers of schedulable commands must be safe to run twice. The jobs library prunes finished scheduling jobs after `JobsOptions.FinishedJobRetention`.

## Migrations

An app that runs `DatabaseInitializer.MigrateAsync` gets the Scheduling migrations, in this order:

1. `InitialScheduling`: the `scheduling.schedules` table.
2. `AddSchedulingJobs`: the `scheduling.jobs` table with `JobsTable.CreateSql`, the prune index and the dedupe index.
3. `RenameLastFiredAt`: `last_fired_at` becomes `last_fired_timestamp`.

The package references `Claudalisk.Jobs.PostgreSql` for the table SQL only. An app still chooses its transport through `AddPostgreSqlJobs`.
