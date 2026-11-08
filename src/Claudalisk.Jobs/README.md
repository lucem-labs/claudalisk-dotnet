# Claudalisk.Jobs

Queued work. A job row is written in the unit of work that queues it, workers claim rows, run them and settle them. `Claudalisk.Jobs.PostgreSql` stores the rows in one `jobs` table per module schema.

## Delivery

Delivery is at least once. A worker can die after the handler committed and before anything else is known, and the stale sweep then runs the job again. Write handlers so that running twice is harmless.

Jobs are settled in the same unit of work as the handler's writes: every command, batch, event and rebuild job ends with `IJobWriter.CompleteAsync`, inside that transaction.

## The completion guard

`CompleteAsync` updates the row only `WHERE id = @id AND status = 'running' AND attempts = @attempt`, where `@attempt` is the attempt this run claimed (`IJobContext.Attempt`). When it updates no row, the run was settled by someone else: a rebuild marked the delivery succeeded, the job was canceled, or the stale sweep requeued it and another worker claimed it. Every other write from a run carries the same attempt: the heartbeat, progress, retry, cancel and requeue do nothing once the run no longer owns the row, and a batch's checkpoint has the same guard, so a batch of a run that was taken over rolls back. The writer returns `false`, the runner throws an internal exception that rolls back the handler's writes in the unit of work, leaves the row exactly as it is, writes no retry or failure, and logs at Information.

## Retries

A failed run is retried with exponential backoff while `attempts < max_attempts`: `RetryBaseDelay * 2^(attempt-1)` plus up to `RetryBaseDelay` of jitter, at most `RetryMaxDelay`.

These are not retried; the job fails at once with a problem type in `last_error`:

- `MessageValidationException`: `/problems/jobs/invalid`
- `DomainException`: `/problems/{module}/{slug}`, the module being the exception's own module, resolved as the web package does (`ProblemTypes.For`). The exception's extension members are stored in `last_error` as `extensions` and shown by the router and the history
- `StaleVersionException`: `/problems/precondition-failed`

`DependencyUnavailableException` is retried and its `RetryAfter` replaces the backoff when longer. `JobsOptions.RetryAfterMaxDelay` (default one hour, must be positive) clamps it. `RetryMaxDelay` still bounds the backoff only. `AddClaudaliskJobs` validates the options when the host starts: `CancelCheckInterval`, `PruneInterval`, `FinishedJobRetention`, `RetryAfterMaxDelay`, `HeartbeatInterval`, `StaleAfter` and `RetryMaxDelay` must be positive. `PollInterval` must be positive or `Timeout.InfiniteTimeSpan`. `RetryBaseDelay` may be zero but not negative.

A job canceled while it runs is marked canceled. A job interrupted by shutdown goes back to the queue without using an attempt.

The heartbeat and the cancel check keep running after a failed call; each failure is logged at Warning.

## Deduplication

`JobOptions.DedupeKey` replaces the queued job of the same kind and key: the older queued row is canceled with `/problems/jobs/superseded`. The same key on a different kind does not touch the other job. A running job is not replaced.

Enqueues take `pg_advisory_xact_lock` on (schema, kind, key) before they supersede, so two concurrent enqueues serialise and the second supersedes the first's committed row. A partial unique index `ux_jobs_dedupe (kind, dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued'` is the backstop. A retry, a shutdown requeue or the stale sweep takes the same lock, and if it would put a job back beside a newer queued job of the same kind and key it cancels it as superseded instead; among several stale running jobs of one key only the newest is requeued.

## Lanes and the stale sweep

A job runs on one lane of its module; `Lane(name, workers)` sets the workers per lane. A lane with no workers is only run by `JobRunner.RunNextAsync`. A rebuild's lane has one worker at most.

Every claim first sweeps the module's stale `running` rows, on all lanes and not only the claiming one: a row whose heartbeat is older than `StaleAfter` goes back to `queued`, or to `failed` with `/problems/jobs/stale` when its attempts are used up.

## Outer steps for batch, event and rebuild jobs

A command job is sent through the dispatcher, so every step runs. The other kinds run the outer steps (logging, validation) through `IOuterPipeline` around their own delegate:

| Job kind | Message kind | Scope of the outer steps |
| --- | --- | --- |
| batch | `Command` | once per run, before its first batch; each batch keeps its own unit of work |
| event | `Event` | once, around the delivery's unit of work |
| rebuild | `Command` | once, around the rebuild's unit of work |

A validator for the command or event type therefore fails the job with `/problems/jobs/invalid`, without retry and before any handler writes.

## Startup

Every name given to `AddClaudaliskModuleJobs` must be the name of a module in the `ModuleCatalog` (compared as the kebab-case segment); the hosted check fails startup with the jobs name and the known module names otherwise. It does nothing when no `AddClaudaliskModules` call registered a catalog.

## Migrations

`JobsTable.CreateSql(schema)` is what an app calls from its initial migration and it never changes. Later changes are separate helpers that an app calls from a new migration, each with a drop counterpart:

- `CreatePruneIndexSql` / `DropPruneIndexSql`: index on finished rows
- `CreateRunningIndexSql` / `DropRunningIndexSql`: index on the heartbeat of running rows, which every claim searches for stale jobs. Call it in every module schema; without it each claim scans the table
- `CreateDedupeIndexSql` / `DropDedupeIndexSql`: cancels older queued duplicates of a kind and key (keeping the newest), then replaces `ix_jobs_dedupe` with the unique `ux_jobs_dedupe`. `DropDedupeIndexSql` restores the old index but does not restore the duplicates `CreateDedupeIndexSql` canceled. Call it in every module schema; without it the dedupe lock still works but the backstop is missing
