using System.Text.Json;
using System.Text.RegularExpressions;

namespace Claudalisk.Jobs.PostgreSql;

public static partial class JobsTable
{
    public static string CreateSql(string schema)
    {
        var s = Quote(schema);
        return $"""
            CREATE SCHEMA IF NOT EXISTS {s};
            CREATE TABLE {s}.jobs (
                id uuid PRIMARY KEY,
                kind text NOT NULL,
                payload jsonb NOT NULL,
                lane text NOT NULL,
                status text NOT NULL,
                attempts integer NOT NULL DEFAULT 0,
                max_attempts integer NOT NULL,
                dedupe_key text NULL,
                not_before timestamptz NOT NULL DEFAULT now(),
                created_at timestamptz NOT NULL DEFAULT now(),
                started_at timestamptz NULL,
                heartbeat_at timestamptz NULL,
                finished_at timestamptz NULL,
                progress_fraction double precision NULL,
                progress_step text NULL,
                checkpoint jsonb NULL,
                result_link text NULL,
                last_error jsonb NULL,
                cancel_requested boolean NOT NULL DEFAULT false,
                CONSTRAINT ck_jobs_status CHECK (status IN ('queued', 'running', 'succeeded', 'failed', 'canceled')),
                CONSTRAINT ck_jobs_max_attempts CHECK (max_attempts >= 1)
            );
            CREATE INDEX ix_jobs_claim ON {s}.jobs (lane, status, not_before);
            CREATE INDEX ix_jobs_dedupe ON {s}.jobs (dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued';
            """;
    }

    public static string DropSql(string schema) => $"DROP TABLE {Quote(schema)}.jobs;";

    public static string CreatePruneIndexSql(string schema) =>
        ForEitherColumnNames(schema, columns =>
            $"CREATE INDEX ix_jobs_finished ON {Quote(schema)}.jobs ({columns.Finished}) WHERE status IN ('succeeded', 'failed', 'canceled');");

    public static string DropPruneIndexSql(string schema) => $"DROP INDEX {Quote(schema)}.ix_jobs_finished;";

    public static string CreateRunningIndexSql(string schema) =>
        ForEitherColumnNames(schema, columns =>
            $"CREATE INDEX ix_jobs_running ON {Quote(schema)}.jobs ({columns.Heartbeat}) WHERE status = 'running';");

    public static string DropRunningIndexSql(string schema) => $"DROP INDEX {Quote(schema)}.ix_jobs_running;";

    public static string CreateClaimIndexSql(string schema)
    {
        var s = Quote(schema);
        return ForEitherColumnNames(schema, columns => $"""
            CREATE INDEX ix_jobs_claim_queued ON {s}.jobs (lane, {columns.NotBefore}, {columns.Created}) WHERE status = 'queued';
            DROP INDEX {s}.ix_jobs_claim;
            """);
    }

    public static string DropClaimIndexSql(string schema)
    {
        var s = Quote(schema);
        return ForEitherColumnNames(schema, columns => $"""
            CREATE INDEX ix_jobs_claim ON {s}.jobs (lane, status, {columns.NotBefore});
            DROP INDEX {s}.ix_jobs_claim_queued;
            """);
    }

    public static string CreateDedupeIndexSql(string schema)
    {
        var s = Quote(schema);
        return ForEitherColumnNames(schema, columns => $"""
            UPDATE {s}.jobs
            SET status = 'canceled', {columns.Finished} = now(), last_error = '{SupersededLiteral}'::jsonb
            WHERE id IN (
                SELECT id FROM (
                    SELECT id, row_number() OVER (PARTITION BY kind, dedupe_key ORDER BY {columns.Created} DESC, id DESC) AS rank
                    FROM {s}.jobs
                    WHERE dedupe_key IS NOT NULL AND status = 'queued'
                ) AS queued
                WHERE rank > 1
            );
            DROP INDEX {s}.ix_jobs_dedupe;
            CREATE UNIQUE INDEX ux_jobs_dedupe ON {s}.jobs (kind, dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued';
            """);
    }

    public static string DropDedupeIndexSql(string schema)
    {
        var s = Quote(schema);
        return $"""
            DROP INDEX {s}.ux_jobs_dedupe;
            CREATE INDEX ix_jobs_dedupe ON {s}.jobs (dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued';
            """;
    }

    public static string RenameColumnsSql(string schema) => RenameSql(schema, ColumnNames.Original, ColumnNames.Renamed);

    public static string RestoreColumnsSql(string schema) => RenameSql(schema, ColumnNames.Renamed, ColumnNames.Original);

    private static string RenameSql(string schema, ColumnNames from, ColumnNames to)
    {
        var s = Quote(schema);
        return string.Join(' ', from.All.Zip(to.All, (current, renamed) => $"ALTER TABLE {s}.jobs RENAME COLUMN {current} TO {renamed};"));
    }

    private static string ForEitherColumnNames(string schema, Func<ColumnNames, string> statements) =>
        $"""
        DO $jobs$
        BEGIN
            IF EXISTS (
                SELECT 1 FROM pg_attribute
                WHERE attrelid = '{Quote(schema)}.jobs'::regclass AND attname = '{ColumnNames.Renamed.Created}' AND NOT attisdropped) THEN
                {statements(ColumnNames.Renamed)}
            ELSE
                {statements(ColumnNames.Original)}
            END IF;
        END $jobs$;
        """;

    private sealed record ColumnNames(string NotBefore, string Created, string Started, string Heartbeat, string Finished, string CancelRequested)
    {
        public static ColumnNames Original { get; } = new("not_before", "created_at", "started_at", "heartbeat_at", "finished_at", "cancel_requested");

        public static ColumnNames Renamed { get; } = new(
            "not_before_timestamp", "created_timestamp", "started_timestamp", "heartbeat_timestamp", "finished_timestamp", "is_cancel_requested");

        public string[] All => [NotBefore, Created, Started, Heartbeat, Finished, CancelRequested];
    }

    internal static string SupersededJson { get; } =
        JsonSerializer.Serialize(new JobError("/problems/jobs/superseded", "A newer job with the same key replaced this one.", null), JobQueue.Json);

    private static string SupersededLiteral => SupersededJson.Replace("'", "''", StringComparison.Ordinal);

    internal static string DedupeLockKey(JobsModule module, string kind, string dedupeKey) => $"{module.Schema}\n{kind}\n{dedupeKey}";

    internal static string Channel(string schema) => $"claudalisk_jobs_{Identifier(schema)}";

    internal static string Quote(string schema) => $"\"{Identifier(schema)}\"";

    private static string Identifier(string schema) =>
        IdentifierPattern().IsMatch(schema ?? "")
            ? schema!
            : throw new ArgumentException($"A jobs schema is a lower-case identifier; got '{schema}'.", nameof(schema));

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
