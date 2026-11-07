using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class RegistrationOrderTests
{
    [Fact]
    public void ModulesRegisterAfterTheModulesTheyDependOn()
    {
        var log = new RegistrationLog();

        new ServiceCollection().AddModules(Configurations.Empty, new ShippingModule(log), new OrderingModule(log), new BillingModule(log));

        Assert.Equal([typeof(BillingModule), typeof(OrderingModule), typeof(ShippingModule)], log.Modules);
    }

    [Fact]
    public void ModulesWithoutDependenciesBetweenThemKeepTheirListedOrder()
    {
        var log = new RegistrationLog();

        new ServiceCollection().AddModules(Configurations.Empty, new ConfiguredModule(log), new BillingModule(log));

        Assert.Equal([typeof(ConfiguredModule), typeof(BillingModule)], log.Modules);
    }

    [Fact]
    public async Task TheRegistryListsTheModulesInRegistrationOrder()
    {
        await using var app = TestHosts.Build(new OrderingModule(), new BillingModule());

        var registry = app.Services.GetRequiredService<ModuleRegistry>();

        Assert.Collection(
            registry.Modules,
            module =>
            {
                Assert.Equal(typeof(BillingModule), module.ModuleType);
                Assert.Equal(typeof(BillingContract), module.Contract);
                Assert.Empty(module.Dependencies);
            },
            module =>
            {
                Assert.Equal(typeof(OrderingModule), module.ModuleType);
                Assert.Equal(typeof(OrderingContract), module.Contract);
                Assert.Equal([typeof(BillingContract)], module.Dependencies);
            });
    }

    [Fact]
    public void AModuleRegistersWithItsContractTheCollectionAndTheConfiguration()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection([new KeyValuePair<string, string?>("Payments:Currency", "EUR")])
            .Build();
        var services = new ServiceCollection();
        var module = new ConfiguredModule();

        services.AddModules(configuration, module);

        Assert.Equal("EUR", module.Currency);
        Assert.NotNull(module.Registration);
        Assert.Same(services, module.Registration.Services);
        Assert.Same(configuration, module.Registration.Configuration);
        Assert.Equal(typeof(PaymentsContract), module.Registration.Contract);
    }
}
