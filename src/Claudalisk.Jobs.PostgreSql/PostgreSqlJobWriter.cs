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
        if (job.Options.DedupeKey is not null && scope.Transaction is null)
        {
            if (scope.Connection.State != ConnectionState.Open)
            {
                await scope.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            }

            var local = (await scope.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
            await using var localDisposal = local.ConfigureAwait(false);
            var inserted = await InsertAsync(module, job, local, cancellationToken).ConfigureAwait(false);
            await local.CommitAsync(cancellationToken).ConfigureAwait(false);
            return inserted;
        }

        return await InsertAsync(module, job, scope.Transaction, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Guid> InsertAsync(JobsModule module, NewJob job, NpgsqlTransaction? transaction, CancellationToken cancellationToken)
    {
        var table = $"{JobsTable.Quote(module.Schema)}.jobs";
        var id = Guid.CreateVersion7();
        if (job.Options.DedupeKey is not null)
        {
            var gate = (await CommandAsync(transaction, "SELECT pg_advisory_xact_lock(hashtextextended(@lockKey, 0))", cancellationToken).ConfigureAwait(false));
            await using var gateDisposal = gate.ConfigureAwait(false);
            gate.Parameters.AddWithValue("lockKey", JobsTable.DedupeLockKey(module, job.Kind, job.Options.DedupeKey));
            await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            var supersede = (await CommandAsync(transaction,
                $"""
                UPDATE {table}
                SET status = 'canceled', finished_at = now(),
                    last_error = @superseded::jsonb
                WHERE kind = @kind AND dedupe_key = @key AND status = 'queued'
                """, cancellationToken).ConfigureAwait(false));
            await using var supersedeDisposal = supersede.ConfigureAwait(false);
            supersede.Parameters.AddWithValue("kind", job.Kind);
            supersede.Parameters.AddWithValue("key", job.Options.DedupeKey);
            supersede.Parameters.AddWithValue("superseded", JobsTable.SupersededJson);
            await supersede.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        var insert = await CommandAsync(transaction,
            $"""
            INSERT INTO {table} (id, kind, payload, lane, status, max_attempts, dedupe_key, not_before)
            VALUES (@id, @kind, @payload::jsonb, @lane, 'queued', @max, @key, coalesce(@notBefore, now()));
            SELECT pg_notify('{JobsTable.Channel(module.Schema)}', @lane);
            """, cancellationToken).ConfigureAwait(false);
        await using var insertDisposal = insert.ConfigureAwait(false);
        insert.Parameters.AddWithValue("id", id);
        insert.Parameters.AddWithValue("kind", job.Kind);
        insert.Parameters.AddWithValue("payload", job.Payload);
        insert.Parameters.AddWithValue("lane", job.Options.Lane);
        insert.Parameters.AddWithValue("max", job.Options.MaxAttempts);
        insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text) { Value = (object?)job.Options.DedupeKey ?? DBNull.Value });
        insert.Parameters.Add(new NpgsqlParameter("notBefore", NpgsqlDbType.TimestampTz) { Value = (object?)job.Options.NotBefore?.ToUniversalTime() ?? DBNull.Value });
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return id;
    }

    public async Task<bool> CompleteAsync(JobsModule module, Guid id, int attempt, string? resultLink, CancellationToken cancellationToken)
    {
        var command = (await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET status = 'succeeded', finished_at = now(), heartbeat_at = NULL, result_link = @link, progress_fraction = 1
            WHERE id = @id AND status = 'running' AND attempts = @attempt
            """, cancellationToken).ConfigureAwait(false));
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.Add(new NpgsqlParameter("link", NpgsqlDbType.Text) { Value = (object?)resultLink ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> SaveCheckpointAsync(JobsModule module, Guid id, int attempt, string checkpoint, double? progress, CancellationToken cancellationToken)
    {
        var command = (await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET checkpoint = @checkpoint::jsonb, progress_fraction = coalesce(@progress, progress_fraction)
            WHERE id = @id AND status = 'running' AND attempts = @attempt
            """, cancellationToken).ConfigureAwait(false));
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("attempt", attempt);
        command.Parameters.AddWithValue("checkpoint", checkpoint);
        command.Parameters.Add(new NpgsqlParameter("progress", NpgsqlDbType.Double) { Value = (object?)progress ?? DBNull.Value });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<bool> DeleteFinishedAsync(JobsModule module, Guid id, CancellationToken cancellationToken)
    {
        var command = (await CommandAsync(
            $"DELETE FROM {JobsTable.Quote(module.Schema)}.jobs WHERE id = @id AND status IN ('succeeded', 'failed', 'canceled')", cancellationToken).ConfigureAwait(false));
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("id", id);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<IReadOnlyList<JobDelivery>> LockActiveAsync(JobsModule module, string kind, CancellationToken cancellationToken)
    {
        var command = (await CommandAsync(
            $"SELECT id, payload::text FROM {JobsTable.Quote(module.Schema)}.jobs WHERE kind = @kind AND status IN ('queued', 'running') ORDER BY created_at, id FOR UPDATE",
            cancellationToken).ConfigureAwait(false));
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("kind", kind);
        var deliveries = new List<JobDelivery>();
        var reader = (await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false));
        await using var readerDisposal = reader.ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
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

        var command = (await CommandAsync(
            $"""
            UPDATE {JobsTable.Quote(module.Schema)}.jobs
            SET status = 'succeeded', started_at = coalesce(started_at, now()), finished_at = now(), heartbeat_at = NULL, progress_fraction = 1
            WHERE id = ANY(@ids) AND kind = @kind AND status IN ('queued', 'running')
            """, cancellationToken).ConfigureAwait(false));
        await using var commandDisposal = command.ConfigureAwait(false);
        command.Parameters.AddWithValue("ids", ids.ToArray());
        command.Parameters.AddWithValue("kind", kind);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<NpgsqlCommand> CommandAsync(string sql, CancellationToken cancellationToken) => CommandAsync(scope.Transaction, sql, cancellationToken);

    private async Task<NpgsqlCommand> CommandAsync(NpgsqlTransaction? transaction, string sql, CancellationToken cancellationToken)
    {
        if (scope.Connection.State != ConnectionState.Open)
        {
            await scope.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        return new NpgsqlCommand(sql, scope.Connection, transaction);
    }
}
