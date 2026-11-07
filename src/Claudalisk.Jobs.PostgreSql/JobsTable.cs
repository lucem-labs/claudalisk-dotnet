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

    internal static string Channel(string schema) => $"claudalisk_jobs_{Identifier(schema)}";

    internal static string Quote(string schema) => $"\"{Identifier(schema)}\"";

    private static string Identifier(string schema) =>
        IdentifierPattern().IsMatch(schema ?? "")
            ? schema!
            : throw new ArgumentException($"A jobs schema is a lower-case identifier; got '{schema}'.", nameof(schema));

    [GeneratedRegex("^[a-z_][a-z0-9_]*$")]
    private static partial Regex IdentifierPattern();
}
