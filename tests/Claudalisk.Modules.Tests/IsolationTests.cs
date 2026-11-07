using Claudalisk.Modules.Tests.Catalog;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class IsolationTests
{
    public static TheoryData<string> Modules() => ["billing", "ordering", "shipping", "catalog"];

    [Theory]
    [MemberData(nameof(Modules))]
    public async Task AModuleComposesWithOnlyItsDeclaredDependencies(string name)
    {
        IModule[] modules = name switch
        {
            "billing" => [new BillingModule()],
            "ordering" => [new OrderingModule(), new BillingModule()],
            "shipping" => [new ShippingModule(), new OrderingModule(), new BillingModule()],
            _ => [new CatalogModule()],
        };

        await using var app = TestHosts.Build(modules);

        Assert.Equal(modules.Length, app.Services.GetRequiredService<ModuleRegistry>().Modules.Count);
    }

    [Fact]
    public async Task AModuleResolvesTheServicesOfItsDependencies()
    {
        await using var app = TestHosts.Build(new OrderingModule(), new BillingModule());

        Assert.Same(app.Services.GetRequiredService<BillingLedger>(), app.Services.GetRequiredService<OrderBook>().Ledger);
        Assert.Null(app.Services.GetService<ShippingQueue>());
    }

    [Fact]
    public void AModuleFromAnotherAssemblyRegistersItsInternalServices()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new CatalogModule());

        Assert.Single(services, descriptor => descriptor.ServiceType.Name == "CatalogPrices" && descriptor.ServiceType.Assembly == typeof(CatalogModule).Assembly);
    }
}
