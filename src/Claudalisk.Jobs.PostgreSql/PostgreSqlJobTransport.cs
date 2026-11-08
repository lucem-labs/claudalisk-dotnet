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
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var claim = new NpgsqlCommand(
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
        claim.Parameters.AddWithValue("lane", lane);
        ClaimedJob? claimed = null;
        await using (var reader = await claim.ExecuteReaderAsync(cancellationToken))
        {
            if (await reader.ReadAsync(cancellationToken))
            {
                claimed = new ClaimedJob(
                    reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetInt32(3), reader.GetInt32(4), reader.IsDBNull(5) ? null : reader.GetString(5));
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task SweepStaleAsync(JobsModule module, TimeSpan staleAfter, CancellationToken cancellationToken)
    {
        var table = $"{JobsTable.Quote(module.Schema)}.jobs";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var requeue = new NpgsqlCommand(
            $"""
            WITH candidates AS (
                SELECT id, kind, dedupe_key,
                    row_number() OVER (PARTITION BY kind, dedupe_key ORDER BY created_at DESC, id DESC) AS rank
                FROM {table}
                WHERE status = 'running' AND heartbeat_at < now() - @stale AND attempts < max_attempts AND dedupe_key IS NOT NULL
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
            WHERE job.status = 'running' AND job.heartbeat_at < now() - @stale
            """, connection);
        requeue.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        requeue.Parameters.AddWithValue("stale", staleAfter);
        requeue.Parameters.AddWithValue("staleError", JsonSerializer.Serialize(new JobError("/problems/jobs/stale", "The job stopped sending heartbeats.", null), JobQueue.Json));
        await requeue.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using (var listen = new NpgsqlCommand($"LISTEN {JobsTable.Channel(module.Schema)}", connection))
        {
            await listen.ExecuteNonQueryAsync(cancellationToken);
        }

        if (await HasQueuedWorkAsync(connection, module, lane, cancellationToken))
        {
            return;
        }

        await connection.WaitAsync(timeout, cancellationToken);
    }

    internal async Task<bool> HasQueuedWorkAsync(JobsModule module, string lane, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        return await HasQueuedWorkAsync(connection, module, lane, cancellationToken);
    }

    private static async Task<bool> HasQueuedWorkAsync(NpgsqlConnection connection, JobsModule module, string lane, CancellationToken cancellationToken)
    {
        await using var pending = new NpgsqlCommand(
            $"SELECT EXISTS (SELECT 1 FROM {JobsTable.Quote(module.Schema)}.jobs WHERE lane = @lane AND status = 'queued' AND not_before <= now())", connection);
        pending.Parameters.AddWithValue("lane", lane);
        return await pending.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<bool> HeartbeatAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET heartbeat_at = now() WHERE id = @id AND status = 'running' RETURNING cancel_requested");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT cancel_requested FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id AND status = 'running'");
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteScalarAsync(cancellationToken) is true;
    }

    public async Task ReportProgressAsync(JobsModule module, Guid id, double fraction, string? step, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET progress_fraction = @fraction, progress_step = @step WHERE id = @id AND status = 'running'");
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("fraction", fraction);
        command.Parameters.Add(new NpgsqlParameter("step", NpgsqlDbType.Text) { Value = (object?)step ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RetryOrFailAsync(JobsModule module, Guid id, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {Table(module)} AS job
            SET last_error = CASE WHEN @retry AND {HasQueuedTwin(module)} THEN @superseded::jsonb ELSE @error::jsonb END,
                heartbeat_at = NULL,
                status = CASE WHEN @retry AND {HasQueuedTwin(module)} THEN 'canceled' WHEN @retry THEN 'queued' ELSE 'failed' END,
                not_before = CASE WHEN @retry THEN now() + @delay ELSE not_before END,
                finished_at = CASE WHEN @retry AND NOT {HasQueuedTwin(module)} THEN NULL ELSE now() END
            WHERE id = @id AND status = 'running'
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        command.Parameters.AddWithValue("error", JsonSerializer.Serialize(error, JobQueue.Json));
        command.Parameters.AddWithValue("retry", retryAfter is not null);
        command.Parameters.AddWithValue("delay", retryAfter ?? TimeSpan.Zero);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await NotifyAsync(module, cancellationToken);
    }

    public async Task MarkCanceledAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"UPDATE {JobsTable.Quote(module.Schema)}.jobs SET status = 'canceled', heartbeat_at = NULL, finished_at = now() WHERE id = @id AND status = 'running'");
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RequeueAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {Table(module)} AS job
            SET status = CASE WHEN {HasQueuedTwin(module)} THEN 'canceled' ELSE 'queued' END,
                finished_at = CASE WHEN {HasQueuedTwin(module)} THEN now() ELSE NULL END,
                last_error = CASE WHEN {HasQueuedTwin(module)} THEN @superseded::jsonb ELSE last_error END,
                heartbeat_at = NULL,
                attempts = greatest(attempts - 1, 0)
            WHERE id = @id AND status = 'running'
            """);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {Columns} FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id");
        command.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(command, cancellationToken);
    }

    public async Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET cancel_requested = true,
                status = CASE WHEN status = 'queued' THEN 'canceled' ELSE status END,
                finished_at = CASE WHEN status = 'queued' THEN now() ELSE finished_at END
            WHERE id = @id AND status IN ('queued', 'running')
            RETURNING {Columns}
            """);
        command.Parameters.AddWithValue("id", id);
        return await ReadOneAsync(command, cancellationToken);
    }

    public async Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken)
    {
        var filter = $"FROM {JobsTable.Quote(module.Schema)}.jobs WHERE (@kind IS NULL OR kind = @kind) AND (@statuses IS NULL OR status = ANY(@statuses))";
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var count = new NpgsqlCommand($"SELECT count(*) {filter}", connection);
        AddFilter(count, query);
        var total = Convert.ToInt32(await count.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
        await using var page = new NpgsqlCommand(
            $"SELECT {Columns}, payload::text {filter} ORDER BY created_at DESC, id DESC OFFSET @offset LIMIT @limit", connection);
        AddFilter(page, query);
        page.Parameters.AddWithValue("offset", (long)(query.Page - 1) * query.PageSize);
        page.Parameters.AddWithValue("limit", query.PageSize);
        var runs = new List<JobRun>();
        await using (var reader = await page.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                runs.Add(new JobRun(Read(reader), reader.GetString(18)));
            }
        }

        return new JobHistoryPage(runs, total);
    }

    public async Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"DELETE FROM {JobsTable.Quote(module.Schema)}.jobs WHERE status IN ('succeeded', 'failed', 'canceled') AND finished_at < now() - @retention");
        command.Parameters.AddWithValue("retention", retention);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string Table(JobsModule module) => $"{JobsTable.Quote(module.Schema)}.jobs";

    private static string HasQueuedTwin(JobsModule module) =>
        $"(job.dedupe_key IS NOT NULL AND EXISTS (SELECT 1 FROM {Table(module)} AS twin WHERE twin.kind = job.kind AND twin.dedupe_key = job.dedupe_key AND twin.status = 'queued'))";

    internal async Task NotifyAsync(JobsModule module, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT pg_notify('{JobsTable.Channel(module.Schema)}', '')");
        await command.ExecuteNonQueryAsync(cancellationToken);
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
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
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
