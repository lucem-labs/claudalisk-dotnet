using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Persistence.Tests;

public sealed class PersistenceSetupTests
{
    [Fact]
    public async Task AStaleSaveBecomesAConcurrencyConflict()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var id = Guid.NewGuid();
        await using (var scope = app.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderStore>().PlaceAsync(id, "A-1", TestContext.Current.CancellationToken);
        }

        await using (var scope = app.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<OrderStore>().RenameAsync(id, "A-2", readVersion: 1, TestContext.Current.CancellationToken);
        }

        await using (var scope = app.CreateAsyncScope())
        {
            var conflict = await Assert.ThrowsAsync<ConcurrencyConflictException>(
                () => scope.ServiceProvider.GetRequiredService<OrderStore>().RenameAsync(id, "A-3", readVersion: 1, TestContext.Current.CancellationToken));
            Assert.IsType<DbUpdateConcurrencyException>(conflict.InnerException);
        }
    }

    [Fact]
    public async Task TheInitializerMigratesEveryModuleContextInRegistrationOrder()
    {
        await using var app = await PostgresServer.CreateEmptyAppAsync();
        await using var scope = app.CreateAsyncScope();
        var initializer = scope.ServiceProvider.GetRequiredService<DatabaseInitializer>();

        Assert.Equal([typeof(OrdersDbContext), typeof(BillingDbContext)], initializer.Contexts.Select(context => context.ContextType));
        Assert.Equal(
            ["orders: 20261026000000_InitialOrders", "billing: 20261026000000_InitialBilling"],
            await initializer.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));

        await initializer.MigrateAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await initializer.GetPendingMigrationsAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void RegisteringAModuleContextTwiceRegistersItOnce()
    {
        var services = new ServiceCollection()
            .AddPersistence("Host=localhost;Database=unused")
            .AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema)
            .AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema)
            .AddPersistence("Host=localhost;Database=unused");

        Assert.Single(services, descriptor => descriptor.ImplementationInstance is ModuleDbContextRegistration);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(OrdersDbContext));
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(IUnitOfWork));
    }

    [Fact]
    public void APooledModuleContextFailsWithAClearMessage()
    {
        var services = new ServiceCollection()
            .AddPersistence("Host=localhost;Database=unused")
            .AddDbContextPool<BillingDbContext>((provider, options) => options.UseModuleSchema(provider, BillingDbContext.Schema));
        using var app = services.BuildServiceProvider();
        using var scope = app.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<BillingDbContext>().Model);

        Assert.Contains("AddDbContextPool", error.Message);
    }

    [Fact]
    public void AModuleContextThatRetriesOnFailureFailsWithAClearMessage()
    {
        var services = new ServiceCollection()
            .AddPersistence("Host=localhost;Database=unused")
            .AddDbContext<BillingDbContext>((provider, options) => options
                .UseModuleSchema(provider, BillingDbContext.Schema)
                .UseNpgsql(npgsql => npgsql.EnableRetryOnFailure()));
        using var app = services.BuildServiceProvider();
        using var scope = app.CreateScope();

        var error = Assert.Throws<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<BillingDbContext>().Model);

        Assert.Contains("EnableRetryOnFailure", error.Message);
    }

    [Fact]
    public async Task WithoutDockerTheTestsFailAndSayDockerIsNeeded()
    {
        var unreachable = new InvalidOperationException("Docker is either not running or misconfigured.");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresServer.StartOrExplainAsync(() => Task.FromException(unreachable)));

        Assert.StartsWith("The database tests need Docker", failure.Message);
        Assert.Same(unreachable, failure.InnerException);
    }
}
