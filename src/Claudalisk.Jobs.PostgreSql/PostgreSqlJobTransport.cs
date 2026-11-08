using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Claudalisk.Jobs.PostgreSql;

internal sealed class PostgreSqlJobTransport(NpgsqlDataSource dataSource) : IJobTransport
{
    internal const string Columns =
        "id, kind, lane, status, attempts, max_attempts, progress_fraction, progress_step, checkpoint::text, result_link, last_error::text, dedupe_key, cancel_requested, created_at, not_before, started_at, heartbeat_at, finished_at";

    public async Task<ClaimedJob?> ClaimAsync(JobsModule module, string lane, CancellationToken cancellationToken)
    {
        var table = $"{JobsTable.Quote(module.Schema)}.jobs";
        var connection = (await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        await using var connectionDisposal = connection.ConfigureAwait(false);
        var transaction = (await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
        await using var transactionDisposal = transaction.ConfigureAwait(false);
        var claim = new NpgsqlCommand(
            $"""
            UPDATE {table} AS job
            SET status = 'running', attempts = job.attempts + 1, started_at = coalesce(job.started_at, now()), heartbeat_at = now()
            FROM (
                SELECT id FROM {table}
                WHERE lane = @lane AND status = 'queued' AND not_before <= now()
                ORDER BY not_before, created_at
                FOR UPDATE SKIP LOCKED
                LIMIT 1
            ) AS next
            WHERE job.id = next.id
            RETURNING job.id, job.kind, job.payload::text, job.attempts, job.max_attempts, job.checkpoint::text
            """, connection, transaction);
        await using var claimDisposal = claim.ConfigureAwait(false);
        claim.Parameters.AddWithValue("lane", lane);
        ClaimedJob? claimed = null;
        var reader = await claim.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                claimed = new ClaimedJob(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return claimed;
    }

    public async Task SweepStaleAsync(JobsModule module, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var connection = (await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        await using var connectionDisposal = connection.ConfigureAwait(false);
        await SweepAsync(connection, null, module, staleAfter, null, cancellationToken).ConfigureAwait(false);
        foreach (var (kind, key) in await StaleDedupeKeysAsync(connection, module, staleAfter, cancellationToken).ConfigureAwait(false))
        {
            var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await using var transactionDisposal = transaction.ConfigureAwait(false);
            var gate = new NpgsqlCommand("SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0))", connection, transaction);
            await using (gate.ConfigureAwait(false))
            {
                gate.Parameters.AddWithValue("lockKey", JobsTable.DedupeLockKey(module, kind, key));
                await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await SweepAsync(connection, transaction, module, staleAfter, (kind, key), cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<List<(string Kind, string Key)>> StaleDedupeKeysAsync(NpgsqlConnection connection, JobsModule module, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var select = new NpgsqlCommand(
            $"SELECT DISTINCT kind, dedupe_key FROM {Table(module)} WHERE status = 'running' AND heartbeat_at < now() - @stale AND dedupe_key IS NOT NULL",
            connection);
        await using var selectDisposal = select.ConfigureAwait(false);
        select.Parameters.AddWithValue("stale", staleAfter);
        var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using var readerDisposal = reader.ConfigureAwait(false);
        var keys = new List<(string Kind, string Key)>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            keys.Add((reader.GetString(0), reader.GetString(1)));
        }

        return keys;
    }

    private static async Task SweepAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, JobsModule module, TimeSpan staleAfter, (string Kind, string Key)? dedupe, CancellationToken cancellationToken)
    {
        var table = Table(module);
        var candidateFilter = dedupe is null ? "AND false" : "AND kind = @kind AND dedupe_key = @key";
        var jobFilter = dedupe is null ? "AND job.dedupe_key IS NULL" : "AND job.kind = @kind AND job.dedupe_key = @key";
        var requeue = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT id, kind, dedupe_key,
                    row_number() OVER (PARTITION BY kind, dedupe_key ORDER BY created_at DESC, id DESC) AS rank
                FROM {table}
                WHERE status = 'running' AND heartbeat_at < now() - @stale AND attempts < max_attempts AND dedupe_key IS NOT NULL {candidateFilter}
            ),
            superseded AS (
                SELECT candidate.id FROM candidates AS candidate
                WHERE candidate.rank > 1
                    OR EXISTS (SELECT 1 FROM {table} AS twin WHERE twin.kind = candidate.kind AND twin.dedupe_key = candidate.dedupe_key AND twin.status = 'queued')
            )
            UPDATE {table} AS job
            SET status = CASE
                    WHEN job.attempts >= job.max_attempts THEN 'failed'
                    WHEN job.id IN (SELECT id FROM superseded) THEN 'canceled'
                    ELSE 'queued' END,
                heartbeat_at = NULL,
                finished_at = CASE WHEN job.attempts >= job.max_attempts OR job.id IN (SELECT id FROM superseded) THEN now() ELSE NULL END,
                last_error = CASE
                    WHEN job.attempts >= job.max_attempts THEN @staleError::jsonb
                    WHEN job.id IN (SELECT id FROM superseded) THEN @superseded::jsonb
                    ELSE job.last_error END
            WHERE job.status = 'running' AND job.heartbeat_at < now() - @stale {jobFilter}
            """, connection, transaction);
        await using var requeueDisposal = requeue.ConfigureAwait(false);
        if (dedupe is { } target)
        {
            requeue.Parameters.AddWithValue("kind", target.Kind);
            requeue.Parameters.AddWithValue("key", target.Key);
        }

        requeue.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        requeue.Parameters.AddWithValue("stale", staleAfter);
        requeue.Parameters.AddWithValue("staleError", JsonSerializer.Serialize(new JobError("/problems/jobs/stale", "The job stopped sending heartbeats.", null), JobQueue.Json));
        await requeue.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var connection = (await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        await using var connectionDisposal = connection.ConfigureAwait(false);
        var listen = new NpgsqlCommand($"LISTEN {JobsTable.Channel(module.Schema)}", connection);
        await using (listen.ConfigureAwait(false))
        {
            await listen.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (await HasQueuedWorkAsync(connection, module, lane, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        await connection.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> HasQueuedWorkAsync(JobsModule module, string lane, CancellationToken cancellationToken)
    {
        var connection = (await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        await using var connectionDisposal = connection.ConfigureAwait(false);
        return await HasQueuedWorkAsync(connection, module, lane, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<bool> HasQueuedWorkAsync(NpgsqlConnection connection, JobsModule module, string lane, CancellationToken cancellationToken)
    {
        var pending = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {JobsTable.Quote(module.Schema)}.jobs WHERE lane = @lane AND status = 'queued' AND not_before <= now())", connection);
        await using var pendingDisposal = pending.ConfigureAwait(false);
        pending.Parameters.AddWithValue("lane", lane);
        return await pending.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<bool> HeartbeatAsync(JobsModule module, Guid id, int attempt, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET heartbeat_at = now() WHERE id = @id AND status = 'running' AND attempts = @attempt RETURNING cancel_requested");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, int attempt, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"SELECT cancel_requested FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id AND status = 'running' AND attempts = @attempt");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is true;
    }

    public async Task ReportProgressAsync(JobsModule module, Guid id, int attempt, double fraction, string? step, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET progress_fraction = @fraction, progress_step = @step WHERE id = @id AND status = 'running' AND attempts = @attempt");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("fraction", fraction);
        command.Parameters.Add(new NpgsqlParameter("step", NpgsqlDbType.Text) { Value = (object?)step ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RetryOrFailAsync(JobsModule module, Guid id, int attempt, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionDisposal = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionDisposal = transaction.ConfigureAwait(false);
        await LockDedupeKeyAsync(connection, transaction, module, id, cancellationToken).ConfigureAwait(false);
        var command = new NpgsqlCommand(
            $"""
            UPDATE {Table(module)} AS job
            SET last_error = CASE WHEN @retry AND {HasQueuedTwin(module)} THEN @superseded::jsonb ELSE @error::jsonb END,
                heartbeat_at = NULL,
                status = CASE WHEN @retry AND {HasQueuedTwin(module)} THEN 'canceled' WHEN @retry THEN 'queued' ELSE 'failed' END,
                not_before = CASE WHEN @retry THEN now() + @delay ELSE not_before END,
                finished_at = CASE WHEN @retry AND NOT {HasQueuedTwin(module)} THEN NULL ELSE now() END
            WHERE id = @id AND status = 'running' AND attempts = @attempt
            """, connection, transaction);
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        command.Parameters.AddWithValue("error", JsonSerializer.Serialize(error, JobQueue.Json));
        command.Parameters.AddWithValue("retry", retryAfter is not null);
        command.Parameters.AddWithValue("delay", retryAfter ?? TimeSpan.Zero);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        await NotifyAsync(module, cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkCanceledAsync(JobsModule module, Guid id, int attempt, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET status = 'canceled', heartbeat_at = NULL, finished_at = now() WHERE id = @id AND status = 'running' AND attempts = @attempt");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RequeueAsync(JobsModule module, Guid id, int attempt, CancellationToken cancellationToken)
    {
        var connection = await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var connectionDisposal = connection.ConfigureAwait(false);
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var transactionDisposal = transaction.ConfigureAwait(false);
        await LockDedupeKeyAsync(connection, transaction, module, id, cancellationToken).ConfigureAwait(false);
        var command = new NpgsqlCommand(
            $"""
            UPDATE {Table(module)} AS job
            SET status = CASE WHEN {HasQueuedTwin(module)} THEN 'canceled' ELSE 'queued' END,
                finished_at = CASE WHEN {HasQueuedTwin(module)} THEN now() ELSE NULL END,
                last_error = CASE WHEN {HasQueuedTwin(module)} THEN @superseded::jsonb ELSE last_error END,
                heartbeat_at = NULL,
                attempts = greatest(attempts - 1, 0)
            WHERE id = @id AND status = 'running' AND attempts = @attempt
            """, connection, transaction);
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task LockDedupeKeyAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        var gate = new NpgsqlCommand(
            $"SELECT pg_advisory_xact_lock(hashtextextended(@schema || chr(10) || kind || chr(10) || dedupe_key, 0)) FROM {Table(module)} WHERE id = @id AND dedupe_key IS NOT NULL",
            connection,
            transaction);
        await using var gateDisposal = gate.ConfigureAwait(false);
        gate.Parameters.AddWithValue("schema", module.Schema);
        gate.Parameters.AddWithValue("id", id);
        await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand($"SELECT {Columns} FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET cancel_requested = true,
                status = CASE WHEN status = 'queued' THEN 'canceled' ELSE status END,
                finished_at = CASE WHEN status = 'queued' THEN now() ELSE finished_at END
            WHERE id = @id AND status IN ('queued', 'running')
            RETURNING {Columns}
            """);
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        var filter = $"FROM {JobsTable.Quote(module.Schema)}.jobs WHERE (@kind IS NULL OR kind = @kind) AND (@statuses IS NULL OR status = ANY(@statuses))";
        var connection = (await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        await using var connectionDisposal = connection.ConfigureAwait(false);
        var count = new NpgsqlCommand($"SELECT count(*) {filter}", connection);
        await using var countDisposal = count.ConfigureAwait(false);
        AddFilter(count, query);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), System.Globalization.CultureInfo.InvariantCulture);
        var page = new NpgsqlCommand(
            $"SELECT {Columns}, payload::text {filter} ORDER BY created_at DESC, id DESC OFFSET @offset LIMIT @limit", connection);
        await using var pageDisposal = page.ConfigureAwait(false);
        AddFilter(page, query);
        page.Parameters.AddWithValue("offset", (long)(query.Page - 1) * query.PageSize);
        page.Parameters.AddWithValue("limit", query.PageSize);
        var runs = new List<JobRun>();
        var reader = await page.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        await using (reader.ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                runs.Add(new JobRun(Read(reader), reader.GetString(18)));
            }
        }

        return new JobHistoryPage(runs, total);
    }

    public async Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand(
            $"DELETE FROM {JobsTable.Quote(module.Schema)}.jobs WHERE status IN ('succeeded', 'failed', 'canceled') AND finished_at < now() - @retention");
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("retention", retention);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Table(JobsModule module) => $"{JobsTable.Quote(module.Schema)}.jobs";

    private static string HasQueuedTwin(JobsModule module) =>
        $"(job.dedupe_key IS NOT NULL AND EXISTS (SELECT 1 FROM {Table(module)} AS twin WHERE twin.kind = job.kind AND twin.dedupe_key = job.dedupe_key AND twin.status = 'queued'))";

    internal async Task NotifyAsync(JobsModule module, CancellationToken cancellationToken)
    {
        var command = dataSource.CreateCommand($"SELECT pg_notify('{JobsTable.Channel(module.Schema)}', '')");
        await using var commandDisposal = command.ConfigureAwait(false);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddFilter(NpgsqlCommand command, JobHistoryQuery query)
    {
        command.Parameters.Add(new NpgsqlParameter("kind", NpgsqlDbType.Text) { Value = (object?)query.Kind ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("statuses", NpgsqlDbType.Array | NpgsqlDbType.Text)
        {
            Value = query.Statuses is { Count: > 0 } statuses
                ? statuses.Select(status => status.ToString().ToLowerInvariant()).ToArray()
                : DBNull.Value,
        });
    }

    private static async Task<JobRecord?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var reader = (await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false));
        await using var readerDisposal = reader.ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    private static JobRecord Read(NpgsqlDataReader reader) =>
        new(
            reader.GetGuid(0),
            reader.GetString(1),
            reader.GetString(2),
            Enum.Parse<JobStatus>(reader.GetString(3), ignoreCase: true),
            reader.GetInt32(4),
            reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetDouble(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.IsDBNull(9) ? null : reader.GetString(9),
            reader.IsDBNull(10) ? null : JsonSerializer.Deserialize<JobError>(reader.GetString(10), JobQueue.Json),
            reader.IsDBNull(11) ? null : reader.GetString(11),
            reader.GetBoolean(12),
            reader.GetFieldValue<DateTimeOffset>(13),
            reader.GetFieldValue<DateTimeOffset>(14),
            reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15),
            reader.IsDBNull(16) ? null : reader.GetFieldValue<DateTimeOffset>(16),
            reader.IsDBNull(17) ? null : reader.GetFieldValue<DateTimeOffset>(17));
}
