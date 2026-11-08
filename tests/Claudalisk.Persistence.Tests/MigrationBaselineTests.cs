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
            .AddClaudaliskMigrationBaseline(OrdersDbContext.Schema, Baseline, Replaced)
            .AddClaudaliskMigrationBaseline(BillingDbContext.Schema, "20261101000000_BillingBaseline", ["20261026000000_InitialBilling", "20261027000000_Missing"]));
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
        Assert.Throws<ArgumentException>(() => services.AddClaudaliskMigrationBaseline("orders", Baseline, []));

        services.AddClaudaliskMigrationBaseline("orders", Baseline, Replaced).AddClaudaliskMigrationBaseline("orders", Baseline, Replaced);

        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MigrationBaseline);
    }

    [Fact]
    public void ASecondBaselineForASchemaWithAnotherMigrationOrReplacedListIsRejected()
    {
        var services = new ServiceCollection().AddClaudaliskMigrationBaseline("orders", Baseline, Replaced);

        var otherMigration = Assert.Throws<InvalidOperationException>(
            () => services.AddClaudaliskMigrationBaseline("orders", "20261102000000_OrdersBaseline", Replaced));
        var otherList = Assert.Throws<InvalidOperationException>(
            () => services.AddClaudaliskMigrationBaseline("orders", Baseline, [.. Replaced, "20261028000000_Extra"]));

        Assert.Contains(Baseline, otherMigration.Message);
        Assert.Contains("20261102000000_OrdersBaseline", otherMigration.Message);
        Assert.Contains("20261028000000_Extra", otherList.Message);
        Assert.Single(services, descriptor => descriptor.ImplementationInstance is MigrationBaseline);
    }

    [Fact]
    public async Task AdoptingWhileAnotherHostHoldsTheLockReadsTheHistoryAgainAfterTheLockIsFree()
    {
        var database = await TransitionalDatabaseAsync(Replaced);
        await using var holder = new NpgsqlConnection(database);
        await holder.OpenAsync(TestContext.Current.CancellationToken);
        await using var transaction = await holder.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockCommand = new NpgsqlCommand($"SELECT pg_advisory_xact_lock({MigrationBaselines.AdvisoryLockKey})", holder, transaction))
        {
            await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var adopting = MigrateWithBaselineAsync(database);
        const string waiting = "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";
        var attempts = 0;
        while (await ScalarLongAsync(database, waiting) == 0)
        {
            Assert.False(adopting.IsCompleted, "Adoption finished without waiting for the lock.");
            Assert.True(++attempts < 3000, "Adoption never waited for the lock.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        foreach (var migration in Replaced)
        {
            await using var delete = new NpgsqlCommand($"DELETE FROM orders.\"__EFMigrationsHistory\" WHERE migration_id = '{migration}'", holder, transaction);
            await delete.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var insert = new NpgsqlCommand($"INSERT INTO orders.\"__EFMigrationsHistory\" (migration_id, product_version) VALUES ('{Baseline}', '10.0.0')", holder, transaction))
        {
            await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await transaction.CommitAsync(TestContext.Current.CancellationToken);
        await adopting;

        Assert.Equal([Baseline], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task TwoHostsAdoptingTheSameBaselineConcurrentlyBothSucceed()
    {
        var database = await TransitionalDatabaseAsync(Replaced);

        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => MigrateWithBaselineAsync(database), TestContext.Current.CancellationToken)));

        Assert.Equal([Baseline], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task MigratingInsideAUnitOfWorkIsRefusedBeforeAnythingChanges()
    {
        var database = await TransitionalDatabaseAsync(Replaced);
        await using var app = PostgresServer.CreateApp(database, services => services.AddClaudaliskMigrationBaseline(OrdersDbContext.Schema, Baseline, Replaced));
        await using var scope = app.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(
                token => initializer.MigrateAsync(token), TestContext.Current.CancellationToken));

        Assert.Contains("cannot run inside a unit of work", refused.Message);
        Assert.Equal(Replaced, await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task ARecordedBaselineWithAReplacedMigrationStillInTheHistoryIsRefused()
    {
        var database = await TransitionalDatabaseAsync([Baseline, Replaced[0]]);

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(database));

        Assert.Contains($"Replaced: {Replaced[0]}", refused.Message);
        Assert.Equal([Replaced[0], Baseline], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task ARecordedBaselineNextToAMigrationItsContextDoesNotKnowIsRefused()
    {
        var database = await TransitionalDatabaseAsync([Baseline, "20261201000000_Stray"]);

        var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(database));

        Assert.Contains("Unknown: 20261201000000_Stray", refused.Message);
        Assert.Equal([Baseline, "20261201000000_Stray"], await HistoryAsync(database, OrdersDbContext.Schema));
    }

    [Fact]
    public async Task TablesTheMigratingRoleCannotSeeStillCountAsTables()
    {
        var database = await PostgresServer.CreateEmptyDatabaseAsync();
        var role = $"baseline_{Guid.NewGuid():N}";
        var name = new NpgsqlConnectionStringBuilder(database).Database;
        await ExecuteAsync(database, $"CREATE SCHEMA orders; CREATE TABLE orders.orders (id uuid PRIMARY KEY); CREATE ROLE {role} LOGIN PASSWORD 'pw'; GRANT CONNECT ON DATABASE \"{name}\" TO {role}; GRANT USAGE, CREATE ON SCHEMA orders TO {role}");
        var limited = new NpgsqlConnectionStringBuilder(database) { Username = role, Password = "pw" }.ConnectionString;

        try
        {
            var refused = await Assert.ThrowsAsync<MigrationBaselineException>(() => MigrateWithBaselineAsync(limited));

            Assert.Contains("has tables but no migration history", refused.Message);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(database, $"DROP OWNED BY {role}; DROP ROLE {role}");
        }
    }

    [Fact]
    public void ABaselineRejectsDuplicateReplacedMigrationsAndReplacingItself()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() => services.AddClaudaliskMigrationBaseline("orders", Baseline, [Replaced[0], Replaced[0]]));
        Assert.Throws<ArgumentException>(() => services.AddClaudaliskMigrationBaseline("orders", Baseline, [Replaced[0], Baseline]));
        Assert.Empty(services);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    public void ABaselineRejectsABlankReplacedMigration(string? blank)
    {
        Assert.ThrowsAny<ArgumentException>(() => new ServiceCollection().AddClaudaliskMigrationBaseline("orders", Baseline, [Replaced[0], blank!]));
    }

    [Theory]
    [InlineData("Orders")]
    [InlineData("or ders")]
    [InlineData("orders\n")]
    [InlineData("orders\"; DROP SCHEMA x")]
    public void ABaselineSchemaMustBeALowerCaseIdentifier(string schema)
    {
        Assert.Throws<ArgumentException>(() => new ServiceCollection().AddClaudaliskMigrationBaseline(schema, Baseline, Replaced));
    }

    private static async Task MigrateWithBaselineAsync(string database)
    {
        await using var app = PostgresServer.CreateApp(database, services => services
            .AddClaudaliskMigrationBaseline(OrdersDbContext.Schema, Baseline, Replaced));
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

    private static async Task<long> ScalarLongAsync(string database, string sql)
    {
        await using var connection = new NpgsqlConnection(database);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (long)(await command.ExecuteScalarAsync())!;
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
