using Claudalisk.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Claudalisk.Scheduling.Tests;

public sealed class MigrationTests
{
    [Fact]
    public async Task AFreshDatabaseMigratesUpDownToZeroAndUpAgain()
    {
        await using var app = TestApp.Build(await TestApp.CreateEmptyDatabaseAsync(), new FakeTimeProvider(TestApp.Start), new Refreshes(), enabled: false);
        await using var scope = app.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var dataSource = app.GetRequiredService<NpgsqlDataSource>();

        await initializer.MigrateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["last_fired_timestamp", "name"], await ColumnsAsync(dataSource, "schedules"));
        Assert.Contains("ux_jobs_dedupe", await IndexesAsync(dataSource), StringComparer.Ordinal);

        await db.Database.GetService<IMigrator>().MigrateAsync("0", TestContext.Current.CancellationToken);
        Assert.Empty(await ColumnsAsync(dataSource, "schedules"));
        Assert.Empty(await ColumnsAsync(dataSource, "jobs"));

        await initializer.MigrateAsync(TestContext.Current.CancellationToken);
        Assert.Equal(["last_fired_timestamp", "name"], await ColumnsAsync(dataSource, "schedules"));
        Assert.Contains("ix_jobs_finished", await IndexesAsync(dataSource), StringComparer.Ordinal);
        Assert.Empty(await initializer.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ColumnsAsync(NpgsqlDataSource dataSource, string table)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT column_name FROM information_schema.columns WHERE table_schema = 'scheduling' AND table_name = @table AND column_name IN ('name', 'last_fired_timestamp', 'last_fired_at', 'id') ORDER BY column_name");
        command.Parameters.AddWithValue("table", table);
        return await ReadAsync(command);
    }

    private static async Task<IReadOnlyList<string>> IndexesAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("SELECT indexname FROM pg_indexes WHERE schemaname = 'scheduling'");
        return await ReadAsync(command);
    }

    private static async Task<IReadOnlyList<string>> ReadAsync(NpgsqlCommand command)
    {
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
