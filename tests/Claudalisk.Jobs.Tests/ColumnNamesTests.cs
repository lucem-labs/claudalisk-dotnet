using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Jobs.Tests;

public sealed class ColumnNamesTests
{
    private static readonly string[] Renamed =
    [
        "created_timestamp", "finished_timestamp", "heartbeat_timestamp", "is_cancel_requested", "not_before_timestamp", "started_timestamp",
    ];

    private static readonly string[] Original = ["cancel_requested", "created_at", "finished_at", "heartbeat_at", "not_before", "started_at"];

    [Theory]
    [InlineData("orders")]
    [InlineData("billing")]
    [InlineData("mail")]
    public async Task AfterTheMigrationsEveryJobsTableHasTheRenamedColumnsAndNoneOfTheOriginalOnes(string schema)
    {
        await using var app = await PostgresServer.CreateAppAsync();

        Assert.Equal(Renamed, await RenamedOrOriginalColumnsAsync(app, schema));
    }

    [Fact]
    public async Task TheIndexesCreatedBeforeTheRenameFollowTheRenamedColumns()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        Assert.Contains("(finished_timestamp)", await IndexAsync(app, "orders", "ix_jobs_finished"), StringComparison.Ordinal);
        Assert.Contains("(heartbeat_timestamp)", await IndexAsync(app, "orders", "ix_jobs_running"), StringComparison.Ordinal);
        Assert.Contains("(lane, not_before_timestamp, created_timestamp)", await IndexAsync(app, "orders", "ix_jobs_claim_queued"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheIndexHelpersWorkOnTheRenamedColumnsAndOnTheOriginalOnes()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await CreateIndexesAsync(app);
        Assert.Contains("(finished_timestamp)", await IndexAsync(app, "billing", "ix_jobs_finished"), StringComparison.Ordinal);
        Assert.Contains("(heartbeat_timestamp)", await IndexAsync(app, "billing", "ix_jobs_running"), StringComparison.Ordinal);
        Assert.Contains("(lane, not_before_timestamp, created_timestamp)", await IndexAsync(app, "billing", "ix_jobs_claim_queued"), StringComparison.Ordinal);
        await DropIndexesAsync(app);
        Assert.Contains("(lane, status, not_before_timestamp)", await IndexAsync(app, "billing", "ix_jobs_claim"), StringComparison.Ordinal);

        await ExecuteAsync(app, PostgreSql.JobsTable.RestoreColumnsSql("billing"));
        Assert.Equal(Original, await RenamedOrOriginalColumnsAsync(app, "billing"));

        await CreateIndexesAsync(app);
        Assert.Contains("(finished_at)", await IndexAsync(app, "billing", "ix_jobs_finished"), StringComparison.Ordinal);
        Assert.Contains("(heartbeat_at)", await IndexAsync(app, "billing", "ix_jobs_running"), StringComparison.Ordinal);
        Assert.Contains("(lane, not_before, created_at)", await IndexAsync(app, "billing", "ix_jobs_claim_queued"), StringComparison.Ordinal);
        await DropIndexesAsync(app);
        Assert.Contains("(lane, status, not_before)", await IndexAsync(app, "billing", "ix_jobs_claim"), StringComparison.Ordinal);

        await ExecuteAsync(app, PostgreSql.JobsTable.RenameColumnsSql("billing"));
        Assert.Equal(Renamed, await RenamedOrOriginalColumnsAsync(app, "billing"));
    }

    [Fact]
    public async Task TheRenameKeepsTheRowsAndTheirValues()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await ExecuteAsync(app, PostgreSql.JobsTable.RestoreColumnsSql("billing"));
        await ExecuteAsync(app, """
            INSERT INTO billing.jobs (id, kind, payload, lane, status, max_attempts, not_before, created_at, started_at, heartbeat_at, finished_at, cancel_requested)
            VALUES (gen_random_uuid(), 'first', '{}', 'default', 'canceled', 5,
                '2026-01-01T01:00:00Z', '2026-01-01T02:00:00Z', '2026-01-01T03:00:00Z', '2026-01-01T04:00:00Z', '2026-01-01T05:00:00Z', true)
            """);

        await ExecuteAsync(app, PostgreSql.JobsTable.RenameColumnsSql("billing"));

        Assert.Equal(
            "01 02 03 04 05 true",
            await ScalarAsync(app, """
                SELECT to_char(not_before_timestamp AT TIME ZONE 'UTC', 'HH24') || ' ' || to_char(created_timestamp AT TIME ZONE 'UTC', 'HH24') || ' '
                    || to_char(started_timestamp AT TIME ZONE 'UTC', 'HH24') || ' ' || to_char(heartbeat_timestamp AT TIME ZONE 'UTC', 'HH24') || ' '
                    || to_char(finished_timestamp AT TIME ZONE 'UTC', 'HH24') || ' ' || is_cancel_requested::text
                FROM billing.jobs
                """));
    }

    private static async Task CreateIndexesAsync(IServiceProvider app)
    {
        await ExecuteAsync(app, PostgreSql.JobsTable.CreatePruneIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.CreateRunningIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.CreateClaimIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.CreateDedupeIndexSql("billing"));
    }

    private static async Task DropIndexesAsync(IServiceProvider app)
    {
        await ExecuteAsync(app, PostgreSql.JobsTable.DropDedupeIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.DropClaimIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.DropRunningIndexSql("billing"));
        await ExecuteAsync(app, PostgreSql.JobsTable.DropPruneIndexSql("billing"));
    }

    private static async Task<string[]> RenamedOrOriginalColumnsAsync(IServiceProvider app, string schema)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = @schema AND table_name = 'jobs' AND column_name = ANY(@names) ORDER BY column_name",
            connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("names", Renamed.Concat(Original).ToArray());
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var names = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            names.Add(reader.GetString(0));
        }

        return [.. names];
    }

    private static async Task<string> IndexAsync(IServiceProvider app, string schema, string index)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT indexdef FROM pg_indexes WHERE schemaname = @schema AND indexname = @index", connection);
        command.Parameters.AddWithValue("schema", schema);
        command.Parameters.AddWithValue("index", index);
        return (string?)await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) ?? "";
    }

    private static async Task<string> ScalarAsync(IServiceProvider app, string sql)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task ExecuteAsync(IServiceProvider app, string sql)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
