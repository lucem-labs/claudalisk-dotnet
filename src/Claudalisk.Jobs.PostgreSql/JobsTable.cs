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
        $"CREATE INDEX ix_jobs_finished ON {Quote(schema)}.jobs (finished_at) WHERE status IN ('succeeded', 'failed', 'canceled');";

    public static string DropPruneIndexSql(string schema) => $"DROP INDEX {Quote(schema)}.ix_jobs_finished;";

    public static string CreateRunningIndexSql(string schema) =>
        $"CREATE INDEX ix_jobs_running ON {Quote(schema)}.jobs (heartbeat_at) WHERE status = 'running';";

    public static string DropRunningIndexSql(string schema) => $"DROP INDEX {Quote(schema)}.ix_jobs_running;";

    public static string CreateDedupeIndexSql(string schema)
    {
        var s = Quote(schema);
        return $"""
            UPDATE {s}.jobs
            SET status = 'canceled', finished_at = now(), last_error = '{SupersededLiteral}'::jsonb
            WHERE id IN (
                SELECT id FROM (
                    SELECT id, row_number() OVER (PARTITION BY kind, dedupe_key ORDER BY created_at DESC, id DESC) AS rank
                    FROM {s}.jobs
                    WHERE dedupe_key IS NOT NULL AND status = 'queued'
                ) AS queued
                WHERE rank > 1
            );
            DROP INDEX {s}.ix_jobs_dedupe;
            CREATE UNIQUE INDEX ux_jobs_dedupe ON {s}.jobs (kind, dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued';
            """;
    }

    public static string DropDedupeIndexSql(string schema)
    {
        var s = Quote(schema);
        return $"""
            DROP INDEX {s}.ux_jobs_dedupe;
            CREATE INDEX ix_jobs_dedupe ON {s}.jobs (dedupe_key) WHERE dedupe_key IS NOT NULL AND status = 'queued';
            """;
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
