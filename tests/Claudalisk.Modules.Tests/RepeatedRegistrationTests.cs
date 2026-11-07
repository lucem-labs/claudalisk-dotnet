using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class RepeatedRegistrationTests
{
    [Fact]
    public void ASecondCallWithTheSameModulesIsANoOp()
    {
        var log = new RegistrationLog();
        var services = new ServiceCollection().AddModules(Configurations.Empty, new BillingModule(log), new OrderingModule(log));
        var before = services.ToList();

        services.AddModules(Configurations.Empty, new OrderingModule(log), new BillingModule(log));

        Assert.Equal(before, services);
        Assert.Equal([typeof(BillingModule), typeof(OrderingModule)], log.Modules);
    }

    [Fact]
    public void AModuleListedTwiceInOneCallRegistersOnce()
    {
        var log = new RegistrationLog();
        var services = new ServiceCollection();

        services.AddModules(Configurations.Empty, new BillingModule(log), new BillingModule(log));

        Assert.Equal([typeof(BillingModule)], log.Modules);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(BillingLedger));
    }

    [Fact]
    public async Task ALaterCallReusesModulesRegisteredEarlier()
    {
        var log = new RegistrationLog();
        IServiceCollection? services = null;

        await using var app = TestHosts.Build(builder =>
        {
            services = builder.Services;
            builder.Services.AddModules(builder.Configuration, new BillingModule(log));
            builder.Services.AddModules(builder.Configuration, new OrderingModule(log));
            builder.Services.AddModules(builder.Configuration, new ShippingModule(log), new BillingModule(log));
        });

        Assert.Equal([typeof(BillingModule), typeof(OrderingModule), typeof(ShippingModule)], log.Modules);
        Assert.Single(services!, descriptor => descriptor.ServiceType == typeof(BillingLedger));
        Assert.Single(services!, descriptor => descriptor.ServiceType == typeof(ModuleRegistry));
        Assert.Equal(3, app.Services.GetRequiredService<ModuleRegistry>().Modules.Count);
        Assert.NotNull(app.Services.GetRequiredService<ShippingQueue>());
    }

    [Fact]
    public void SeparateCollectionsKeepSeparateRegistrations()
    {
        var log = new RegistrationLog();
        var first = new ServiceCollection().AddModules(Configurations.Empty, new BillingModule(log));
        var second = new ServiceCollection();

        second.AddModules(Configurations.Empty, new BillingModule(log));

        Assert.Equal([typeof(BillingModule), typeof(BillingModule)], log.Modules);
        Assert.Single(first, descriptor => descriptor.ServiceType == typeof(BillingLedger));
        Assert.Single(second, descriptor => descriptor.ServiceType == typeof(BillingLedger));
    }

    [Fact]
    public void ARepeatedCallDoesNotAddControllersAgain()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new BillingModule());
        var before = services.ToList();

        services.AddModules(Configurations.Empty, new OrderingModule());

        var added = services.Except(before).ToList();
        Assert.Equal([typeof(OrderBook)], added.Select(descriptor => descriptor.ServiceType));
    }
}
