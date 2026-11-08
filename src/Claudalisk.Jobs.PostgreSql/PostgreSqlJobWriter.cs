using System.Data;
using System.Text.Json;
using Claudalisk.Persistence;
using Npgsql;
using NpgsqlTypes;

namespace Claudalisk.Jobs.PostgreSql;

internal sealed class PostgreSqlJobWriter(IScopeConnection scope) : IJobWriter
{
    public async Task<Guid> InsertAsync(JobsModule module, NewJob job, CancellationToken cancellationToken)
    {
        var table = $"{JobsTable.Quote(module.Schema)}.jobs";
        var id = Guid.CreateVersion7();
        if (job.Options.DedupeKey is not null)
        {
            await using var supersede = await CommandAsync(
                $"""
                UPDATE {table}
                SET status = 'canceled', finished_at = now(),
                    last_error = @superseded::jsonb
                WHERE dedupe_key = @key AND status = 'queued'
                """, cancellationToken);
            supersede.Parameters.AddWithValue("key", job.Options.DedupeKey);
            supersede.Parameters.AddWithValue("superseded", JsonSerializer.Serialize(new JobError("/problems/jobs/superseded", "A newer job with the same key replaced this one.", null), JobQueue.Json));
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var insert = await CommandAsync(
            $"""
            INSERT INTO {table} (id, kind, payload, lane, status, max_attempts, dedupe_key, not_before)
            VALUES (@id, @kind, @payload::jsonb, @lane, 'queued', @max, @key, coalesce(@notBefore, now()));
            SELECT pg_notify('{JobsTable.Channel(module.Schema)}', @lane);
            """, cancellationToken);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("kind", job.Kind);
        insert.Parameters.AddWithValue("payload", job.Payload);
        insert.Parameters.AddWithValue("lane", job.Options.Lane);
        insert.Parameters.AddWithValue("max", job.Options.MaxAttempts);
        insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)job.Options.DedupeKey ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("notBefore", NpgsqlDbType.TimestampTz) { Value = (object?)job.Options.NotBefore?.ToUniversalTime() ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return id;
    }

    public async Task<bool> CompleteAsync(JobsModule module, Guid id, int attempt, string? resultLink, CancellationToken cancellationToken)
    {
        await using var command = await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET status = 'succeeded', finished_at = now(), heartbeat_at = NULL, result_link = @link, progress_fraction = 1
            WHERE id = @id AND status = 'running' AND attempts = @attempt
            """, cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.Add(new NpgsqlParameter("link", NpgsqlDbType.Text) { Value = (object?)resultLink ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task SaveCheckpointAsync(JobsModule module, Guid id, string checkpoint, double? progress, CancellationToken cancellationToken)
    {
        await using var command = await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET checkpoint = @checkpoint::jsonb, progress_fraction = coalesce(@progress, progress_fraction)
            WHERE id = @id
            """, cancellationToken);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("checkpoint", checkpoint);
        command.Parameters.Add(new NpgsqlParameter("progress", NpgsqlDbType.Double) { Value = (object?)progress ?? DBNull.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteFinishedAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        await using var command = await CommandAsync(
            $"DELETE FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id AND status IN ('succeeded', 'failed', 'canceled')", cancellationToken);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<JobDelivery>> LockActiveAsync(JobsModule module, string kind, CancellationToken cancellationToken)
    {
        await using var command = await CommandAsync(
            $"SELECT id, payload::text FROM {JobsTable.Quote(module.Schema)}.jobs WHERE kind = @kind AND status IN ('queued', 'running') ORDER BY created_at, id FOR UPDATE",
            cancellationToken);
        command.Parameters.AddWithValue("kind", kind);
        var deliveries = new List<JobDelivery>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            deliveries.Add(new JobDelivery(reader.GetGuid(0), reader.GetString(1)));
        }

        return deliveries;
    }

    public async Task MarkSucceededAsync(JobsModule module, string kind, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        if (ids.Count == 0)
        {
            return;
        }

        await using var command = await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET status = 'succeeded', started_at = coalesce(started_at, now()), finished_at = now(), heartbeat_at = NULL, progress_fraction = 1
            WHERE id = ANY(@ids) AND kind = @kind AND status IN ('queued', 'running')
            """, cancellationToken);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        command.Parameters.AddWithValue("kind", kind);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken cancellationToken)
    {
        if (scope.Connection.State != ConnectionState.Open)
        {
            await scope.Connection.OpenAsync(cancellationToken);
        }

        return new NpgsqlCommand(sql, scope.Connection, scope.Transaction);
    }
}
