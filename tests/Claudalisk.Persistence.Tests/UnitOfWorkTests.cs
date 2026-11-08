using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Persistence.Tests;

public sealed class UnitOfWorkTests
{
    private sealed record Counts(int Orders, int Charges, int Queued);

    private sealed class CompositionFailed(string message) : Exception(message);

    [Fact]
    public async Task WorkFromTwoModulesCommitsTogether()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await billing.ChargeAsync(orderId, 10m, token);
            }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(1, 1, 0), await CountAsync(app));
    }

    [Fact]
    public async Task ADatabaseErrorInTheSecondModuleRollsBackTheFirst()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await billing.ChargeAsync(orderId, -5m, token);
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(0, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AnExceptionAfterBothSavesRollsBackBoth()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<CompositionFailed>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await billing.ChargeAsync(orderId, 10m, token);
                throw new CompositionFailed("composition rule failed");
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(0, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task WithoutAUnitOfWorkEachModuleCommitsOnItsOwn()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (_, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await orders.PlaceAsync(orderId, "A-1", TestContext.Current.CancellationToken);
            await Assert.ThrowsAsync<DbUpdateException>(() => billing.ChargeAsync(orderId, -5m, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(1, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AMultiStatementSaveJoinsTheUnitOfWorkInsteadOfStartingItsOwn()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceManyAsync([(orderId, "A-1"), (Guid.NewGuid(), "A-2"), (Guid.NewGuid(), "A-3")], token);
                await billing.ChargeAsync(orderId, -5m, token);
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(0, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task ANestedUnitOfWorkJoinsTheOuterOne()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<CompositionFailed>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await unitOfWork.RunAsync(inner => billing.ChargeAsync(orderId, 10m, inner), token);
                throw new CompositionFailed("outer work failed after the nested call");
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(0, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AContextUsedBeforeTheUnitOfWorkStillJoinsIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            Assert.Equal(0, await orders.CountAsync(TestContext.Current.CancellationToken));
            Assert.Equal(0, await billing.CountChargesAsync(TestContext.Current.CancellationToken));
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await billing.ChargeAsync(orderId, -5m, token);
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(new Counts(0, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task WithoutAUnitOfWorkAStoreKeepsTheTransactionItBegan()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            var orders = scope.ServiceProvider.GetRequiredService<OrderStore>();
            await using (var transaction = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken))
            {
                await orders.PlaceAsync(Guid.NewGuid(), "A-1", TestContext.Current.CancellationToken);
                await orders.PlaceAsync(Guid.NewGuid(), "A-2", TestContext.Current.CancellationToken);
                Assert.Same(transaction, db.Database.CurrentTransaction);
                await transaction.RollbackAsync(TestContext.Current.CancellationToken);
            }

            await orders.PlaceAsync(Guid.NewGuid(), "A-3", TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(1, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AContextReadsAfterAUnitOfWorkItWroteIn()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var (unitOfWork, orders, _) = Resolve(scope.ServiceProvider);
        await unitOfWork.RunAsync(token => orders.PlaceAsync(Guid.NewGuid(), "A-1", token), TestContext.Current.CancellationToken);

        Assert.Equal(1, await scope.ServiceProvider.GetRequiredService<OrdersDbContext>().Orders.CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ContextsAreNotLeftOnAFinishedTransaction()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            await unitOfWork.RunAsync(token => orders.PlaceAsync(Guid.NewGuid(), "A-1", token), TestContext.Current.CancellationToken);
            await orders.PlaceAsync(Guid.NewGuid(), "A-2", TestContext.Current.CancellationToken);
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-3", token);
                await billing.ChargeAsync(orderId, -5m, token);
            }, TestContext.Current.CancellationToken));
            await unitOfWork.RunAsync(token => orders.PlaceAsync(Guid.NewGuid(), "A-4", token), TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(3, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AContextCanBeginItsOwnTransactionAfterAUnitOfWork()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, _) = Resolve(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            await unitOfWork.RunAsync(token => orders.PlaceAsync(Guid.NewGuid(), "A-1", token), TestContext.Current.CancellationToken);

            await using var own = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "A-2", Version = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await own.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(2, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task AContextCanBeginItsOwnTransactionAfterARolledBackUnitOfWork()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<OrdersDbContext>();
            var orderId = Guid.NewGuid();
            await Assert.ThrowsAsync<DbUpdateException>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(orderId, "A-1", token);
                await billing.ChargeAsync(orderId, -5m, token);
            }, TestContext.Current.CancellationToken));
            db.ChangeTracker.Clear();

            await using var own = await db.Database.BeginTransactionAsync(TestContext.Current.CancellationToken);
            db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "A-2", Version = 1 });
            await db.SaveChangesAsync(TestContext.Current.CancellationToken);
            await own.CommitAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(1, 0, 0), await CountAsync(app));
    }

    [Fact]
    public async Task QueuedWorkCommitsAndRollsBackWithTheData()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var failed = Guid.NewGuid();
            await Assert.ThrowsAsync<CompositionFailed>(() => unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(failed, "A-1", token);
                await billing.ChargeAsync(failed, 10m, token);
                await billing.EnqueueReceiptAsync(failed, token);
                throw new CompositionFailed("rolled back");
            }, TestContext.Current.CancellationToken));

            var placed = Guid.NewGuid();
            await unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(placed, "A-2", token);
                await billing.ChargeAsync(placed, 10m, token);
                await billing.EnqueueReceiptAsync(placed, token);
            }, TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(1, 1, 1), await CountAsync(app));
    }

    [Fact]
    public async Task UncommittedWorkIsInvisibleToAnotherScope()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var (unitOfWork, orders, _) = Resolve(scope.ServiceProvider);
        await unitOfWork.RunAsync(async token =>
        {
            await orders.PlaceAsync(Guid.NewGuid(), "A-1", token);
            Assert.Equal(1, await orders.CountAsync(token));
            Assert.Equal(0, (await CountAsync(app)).Orders);
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, (await CountAsync(app)).Orders);
    }

    [Fact]
    public async Task EveryModuleContextInAScopeSharesOneConnection()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var first = app.CreateAsyncScope();
        await using var second = app.CreateAsyncScope();

        var orders = first.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.GetDbConnection();
        var billing = first.ServiceProvider.GetRequiredService<BillingDbContext>().Database.GetDbConnection();
        var otherScope = second.ServiceProvider.GetRequiredService<OrdersDbContext>().Database.GetDbConnection();

        Assert.Same(orders, billing);
        Assert.NotSame(orders, otherScope);
        Assert.Same(orders, first.ServiceProvider.GetRequiredService<IScopeConnection>().Connection);
    }

    [Fact]
    public async Task TheScopeConnectionExposesTheUnitOfWorksTransactionOnlyWhileItRuns()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var shared = scope.ServiceProvider.GetRequiredService<IScopeConnection>();

        Assert.Null(shared.Transaction);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(token =>
        {
            Assert.NotNull(shared.Transaction);
            Assert.Same(shared.Connection, shared.Transaction!.Connection);
            return Task.CompletedTask;
        }, TestContext.Current.CancellationToken);
        Assert.Null(shared.Transaction);
    }

    [Fact]
    public async Task ALongLivedScopeKeepsNoEnlistedContextsBetweenUnitsOfWork()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        var (unitOfWork, orders, _) = Resolve(scope.ServiceProvider);
        var session = scope.ServiceProvider.GetRequiredService<DbSession>();

        for (var i = 0; i < 3; i++)
        {
            await unitOfWork.RunAsync(async token =>
            {
                await orders.PlaceAsync(Guid.NewGuid(), $"A-{i}", token);
                Assert.Single(session.EnlistedContexts);
            }, TestContext.Current.CancellationToken);
            Assert.Empty(session.EnlistedContexts);
        }
    }

    [Fact]
    public async Task AContextLeftOutOfTheLastUnitOfWorkIsNotLeftOnAnEarlierOne()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using (var scope = app.CreateAsyncScope())
        {
            var (unitOfWork, orders, billing) = Resolve(scope.ServiceProvider);
            var orderId = Guid.NewGuid();
            await unitOfWork.RunAsync(token => orders.PlaceAsync(orderId, "A-1", token), TestContext.Current.CancellationToken);
            await unitOfWork.RunAsync(token => billing.ChargeAsync(orderId, 10m, token), TestContext.Current.CancellationToken);
            await orders.PlaceAsync(Guid.NewGuid(), "A-2", TestContext.Current.CancellationToken);
        }

        Assert.Equal(new Counts(2, 1, 0), await CountAsync(app));
    }

    [Fact]
    public async Task ANullWorkDelegateIsRejectedBeforeATransactionOpens()
    {
        await using var app = new ServiceCollection().AddTestModules("Host=localhost;Database=unused").BuildServiceProvider();
        await using var scope = app.CreateAsyncScope();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => unitOfWork.RunAsync((Func<CancellationToken, Task>)null!, TestContext.Current.CancellationToken));

        Assert.Empty(scope.ServiceProvider.GetRequiredService<DbSession>().EnlistedContexts);
    }

    [Fact]
    public async Task EachModuleKeepsItsOwnSchemaSnakeCaseNamesAndMigrationsHistory()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var scope = app.CreateAsyncScope();
        await using var connection = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>().CreateConnection();
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Equal(
            ["billing.__EFMigrationsHistory", "billing.charges", "billing.jobs", "orders.__EFMigrationsHistory", "orders.orders"],
            await ReadAsync(connection, """
                SELECT table_schema || '.' || table_name FROM information_schema.tables
                WHERE table_schema NOT IN ('pg_catalog', 'information_schema') ORDER BY 1
                """));
        Assert.Equal(
            ["billing.20261026000000_InitialBilling", "orders.20261026000000_InitialOrders"],
            await ReadAsync(connection, """
                SELECT 'billing.' || migration_id FROM billing."__EFMigrationsHistory"
                UNION ALL SELECT 'orders.' || migration_id FROM orders."__EFMigrationsHistory" ORDER BY 1
                """));
        Assert.Equal("billing.charges", scope.ServiceProvider.GetRequiredService<BillingDbContext>().Model.FindEntityType(typeof(Charge))!.GetSchemaQualifiedTableName());
        Assert.Equal("order_id", scope.ServiceProvider.GetRequiredService<BillingDbContext>().Model.FindEntityType(typeof(Charge))!.FindProperty(nameof(Charge.OrderId))!.GetColumnName());
    }

    private static (IUnitOfWork UnitOfWork, OrderStore Orders, BillingStore Billing) Resolve(IServiceProvider scope) =>
        (scope.GetRequiredService<IUnitOfWork>(), scope.GetRequiredService<OrderStore>(), scope.GetRequiredService<BillingStore>());

    private static async Task<Counts> CountAsync(IServiceProvider app)
    {
        await using var scope = app.CreateAsyncScope();
        var orders = scope.ServiceProvider.GetRequiredService<OrderStore>();
        var billing = scope.ServiceProvider.GetRequiredService<BillingStore>();
        return new Counts(await orders.CountAsync(TestContext.Current.CancellationToken), await billing.CountChargesAsync(TestContext.Current.CancellationToken), await billing.CountQueuedAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<List<string>> ReadAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var values = new List<string>();
        while (await reader.ReadAsync())
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }
}
