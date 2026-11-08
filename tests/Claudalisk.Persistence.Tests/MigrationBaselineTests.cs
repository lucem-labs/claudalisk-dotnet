using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Persistence.Tests;

public sealed class MigrationBaselineTests
{
    private const string Baseline = "20261026000000_InitialOrders";
    private static readonly string[] Replaced = ["20261001000000_CreateOrders", "20261002000000_OrderVersions"];

    [Fact]
    public async Task AFreshDatabaseAppliesTheBaselineLikeAnyMigration()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();

        await MigrateWithBaselineAsync(database);

        Assert.Equal([Baseline], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task ADatabaseWithEveryReplacedMigrationRecordsTheBaselineInTheirPlaceAndKeepsItsData()
    {
        var database = await TransitionalDatabaseAsync(Replaced);
        var order = Guid.NewGuid();
        await ExecuteAsync(database, $"INSERT INTO orders.orders (id, reference, version) VALUES ('{order}', 'A-1', 1)");

        await MigrateWithBaselineAsync(database);
        await MigrateWithBaselineAsync(database);

        Assert.Equal([Baseline], await HistoryAsync(database, OrdersDbContext.Schema));
        Assert.Equal(["20261026000000_InitialBilling"], await HistoryAsync(database, BillingDbContext.Schema));
        Assert.Equal("A-1", await ScalarAsync(database, $"SELECT reference FROM orders.orders WHERE id = '{order}'"));
    }

    [Fact]
    public async Task ADatabaseMissingAReplacedMigrationIsRefusedAndLeftUnchanged()
    {
        var database = await TransitionalDatabaseAsync([Replaced[0]]);

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(database));

        Assert.Contains($"Missing: {Replaced[1]}", refused.Message);
        Assert.Contains("Deploy the last version before the baseline first", refused.Message);
        Assert.Equal([Replaced[0]], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task ADatabaseWithAMigrationTheBaselineDoesNotReplaceIsRefused()
    {
        var database = await TransitionalDatabaseAsync([.. Replaced, "20261003000000_Unknown"]);

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(database));

        Assert.Contains("Unknown: 20261003000000_Unknown", refused.Message);
        Assert.Equal(3, (await HistoryAsync(database, OrdersDbContext.Schema)).Count);
    }

    [Fact]
    public async Task ARefusalInOneSchemaRecordsNoBaselineInAnother()
    {
        var database = await TransitionalDatabaseAsync(Replaced);
        await using var app = PostgresServer.CreateApp(database, services => services
            .AddMigrationBaseline(OrdersDbContext.Schema, Baseline, Replaced)
            .AddMigrationBaseline(BillingDbContext.Schema, "20261101000000_BillingBaseline", ["20261026000000_InitialBilling", "20261027000000_Missing"]));
        await using var scope = app.CreateAsyncScope();

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(
            () => scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Missing: 20261027000000_Missing", refused.Message);
        Assert.Equal(Replaced, await HistoryAsync(database, OrdersDbContext.Schema));
        Assert.Equal(["20261026000000_InitialBilling"], await HistoryAsync(database, BillingDbContext.Schema));
    }

    [Fact]
    public async Task ASchemaWithTablesButNoHistoryIsRefused()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        await ExecuteAsync(database, "CREATE SCHEMA orders; CREATE TABLE orders.orders (id uuid PRIMARY KEY)");

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(database));

        Assert.Contains("has tables but no migration history", refused.Message);
    }

    [Fact]
    public void ABaselineMustReplaceAtLeastOneMigrationAndIsRegisteredOncePerSchema()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddMigrationBaseline("orders", Baseline, []));

        services.AddMigrationBaseline("orders", Baseline, Replaced).AddMigrationBaseline("orders", Baseline, Replaced);

        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MigrationBaseline);
    }

    [Fact]
    public void ASecondBaselineForASchemaWithAnotherMigrationOrReplacedListIsRejected()
    {
        var services = new ServiceCollection().AddMigrationBaseline("orders", Baseline, Replaced);

        var otherMigration = Assert.Throws<InvalidOperationException>(
            () => services.AddMigrationBaseline("orders", "20261102000000_OrdersBaseline", Replaced));
        var otherList = Assert.Throws<InvalidOperationException>(
            () => services.AddMigrationBaseline("orders", Baseline, [.. Replaced, "20261028000000_Extra"]));

        Assert.Contains(Baseline, otherMigration.Message);
        Assert.Contains("20261102000000_OrdersBaseline", otherMigration.Message);
        Assert.Contains("20261028000000_Extra", otherList.Message);
        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MigrationBaseline);
    }

    private static async Task MigrateWithBaselineAsync(string database)
    {
        await using var app = PostgresServer.CreateApp(database, services => services
            .AddMigrationBaseline(OrdersDbContext.Schema, Baseline, Replaced));
        await using var scope = app.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<string> TransitionalDatabaseAsync(string[] history)
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        await using (var app = PostgresServer.CreateApp(database))
        {
            await using var scope = app.CreateAsyncScope();
            await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(TestContext.Current.CancellationToken);
        }

        await ExecuteAsync(database, "DELETE FROM orders.\"__EFMigrationsHistory\"");
        foreach (var migration in history)
        {
            await ExecuteAsync(database, $"INSERT INTO orders.\"__EFMigrationsHistory\" (migration_id, product_version) VALUES ('{migration}', '10.0.0')");
        }

        return database;
    }

    private static Task<List<string>> HistoryAsync(string database, string schema) =>
        ListAsync(database, $"SELECT migration_id FROM {schema}.\"__EFMigrationsHistory\" ORDER BY migration_id");

    private static async Task<List<string>> ListAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<string> ScalarAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
