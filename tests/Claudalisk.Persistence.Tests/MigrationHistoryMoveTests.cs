using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Persistence.Tests;

public sealed class MigrationHistoryMoveTests
{
    [Fact]
    public async Task AHistoryInTheOldSchemaMovesToTheModuleSchemaBeforeMigrating()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        await MigrateAsync(database, moved: false);
        await ExecuteAsync(database, "CREATE SCHEMA shop; ALTER TABLE orders.\"__EFMigrationsHistory\" SET SCHEMA shop");

        await MigrateAsync(database, moved: true);
        await MigrateAsync(database, moved: true);

        Assert.Equal("0", await ScalarAsync(database, "SELECT count(*)::text FROM information_schema.tables WHERE table_schema = 'shop'"));
        Assert.Equal("1", await ScalarAsync(database, "SELECT count(*)::text FROM orders.\"__EFMigrationsHistory\""));
    }

    [Fact]
    public async Task AHistoryInBothSchemasIsRefusedAndLeftUnchanged()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        await MigrateAsync(database, moved: false);
        await ExecuteAsync(database, "CREATE SCHEMA shop; CREATE TABLE shop.\"__EFMigrationsHistory\" (migration_id varchar(150) PRIMARY KEY, product_version varchar(32) NOT NULL)");

        var refused = await Assert.ThrowsAsync<MigrationHistoryMoveException>(() => MigrateAsync(database, moved: true));

        Assert.Contains("Both schema shop and schema orders", refused.Message);
        Assert.Equal("1", await ScalarAsync(database, "SELECT count(*)::text FROM information_schema.tables WHERE table_schema = 'shop'"));
    }

    [Fact]
    public async Task ARefusedMoveUndoesTheMovesBeforeIt()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        await MigrateAsync(database, moved: false);
        await ExecuteAsync(database, "CREATE SCHEMA shop; ALTER TABLE orders.\"__EFMigrationsHistory\" SET SCHEMA shop");
        await ExecuteAsync(database, "CREATE SCHEMA till; CREATE TABLE till.\"__EFMigrationsHistory\" (migration_id varchar(150) PRIMARY KEY, product_version varchar(32) NOT NULL)");
        await using var app = PostgresServer.CreateApp(database, services => services
            .AddMovedMigrationHistory("shop", OrdersDbContext.Schema)
            .AddMovedMigrationHistory("till", BillingDbContext.Schema));
        await using var scope = app.CreateAsyncScope();

        var refused = await Assert.ThrowsAsync<MigrationHistoryMoveException>(
            () => scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(TestContext.Current.CancellationToken));

        Assert.Contains("Both schema till and schema billing", refused.Message);
        Assert.Equal("1", await ScalarAsync(database, "SELECT count(*)::text FROM shop.\"__EFMigrationsHistory\""));
        Assert.Equal("0", await ScalarAsync(database, "SELECT count(*)::text FROM information_schema.tables WHERE table_schema = 'orders' AND table_name = '__EFMigrationsHistory'"));
    }

    [Fact]
    public async Task AFreshDatabaseMigratesAsIfNothingMoved()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();

        await MigrateAsync(database, moved: true);

        Assert.Equal("0", await ScalarAsync(database, "SELECT count(*)::text FROM information_schema.schemata WHERE schema_name = 'shop'"));
        Assert.Equal("1", await ScalarAsync(database, "SELECT count(*)::text FROM orders.\"__EFMigrationsHistory\""));
    }

    [Fact]
    public void AMoveNamesTwoDifferentLowerCaseSchemasAndIsRegisteredOnce()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentException>(() => services.AddMovedMigrationHistory("orders", "orders"));
        Assert.Throws<ArgumentException>(() => services.AddMovedMigrationHistory("Shop", "orders"));

        services.AddMovedMigrationHistory("shop", "orders").AddMovedMigrationHistory("shop", "orders");

        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MovedMigrationHistory);
    }

    [Fact]
    public void AConflictingMoveIsRejected()
    {
        var services = new ServiceCollection().AddMovedMigrationHistory("shop", "orders");

        var otherSource = Assert.Throws<InvalidOperationException>(() => services.AddMovedMigrationHistory("till", "orders"));
        var otherTarget = Assert.Throws<InvalidOperationException>(() => services.AddMovedMigrationHistory("shop", "billing"));

        Assert.Contains("'shop'", otherSource.Message);
        Assert.Contains("'till'", otherSource.Message);
        Assert.Contains("'orders'", otherTarget.Message);
        Assert.Contains("'billing'", otherTarget.Message);
        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MovedMigrationHistory);
    }

    private static async Task MigrateAsync(string database, bool moved)
    {
        await using var app = PostgresServer.CreateApp(database, services =>
        {
            if (moved)
            {
                services.AddMovedMigrationHistory("shop", OrdersDbContext.Schema);
            }
        });
        await using var scope = app.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DatabaseInitializer>().MigrateAsync(TestContext.Current.CancellationToken);
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
