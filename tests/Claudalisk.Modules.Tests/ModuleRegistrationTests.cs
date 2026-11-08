using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class ModuleRegistrationTests
{
    private static readonly IConfiguration Configuration = new ConfigurationBuilder().Build();

    [Fact]
    public void AMissingDependencyNamesTheMarkerAndRegistersNothing()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new OrdersModule()));

        Assert.Contains(typeof(StorageContract).FullName!, error.Message);
        Assert.Contains(typeof(OrdersModule).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void TwoProvidersOfADependencyNameEveryProviderAndRegisterNothing()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddModules(Configuration, new OrdersModule(), new StorageModule(), new OtherStorageModule()));

        Assert.Contains("both provide the contract", error.Message);
        Assert.Contains(typeof(StorageModule).FullName!, error.Message);
        Assert.Contains(typeof(OtherStorageModule).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void ACircularDependencyNamesTheCycleAndRegistersNothing()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddModules(Configuration, new CircularOrdersModule(), new CircularBillingModule()));

        Assert.Contains("Circular", error.Message);
        Assert.Contains(typeof(CircularOrdersModule).FullName!, error.Message);
        Assert.Contains(typeof(CircularBillingModule).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void ADependencyMustBeASealedMarkerClass()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new InterfaceDependencyModule()));

        Assert.Contains(typeof(INotAMarker).FullName!, error.Message);
        Assert.Contains("sealed class", error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void ANullDependencyEntryIsRejectedNamingTheModule()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new NullDependencyModule(), new StorageModule()));

        Assert.Contains(typeof(NullDependencyModule).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void ANullDependenciesCollectionIsRejectedNamingTheModule()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new NullDependenciesModule()));

        Assert.Contains(typeof(NullDependenciesModule).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void AContractMustBeASealedMarkerClass()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new NonSealedContractModule()));

        Assert.Contains(typeof(INotAMarker).FullName!, error.Message);
        Assert.Contains("contract", error.Message);
        Assert.Contains("sealed class", error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void TwoModulesWithTheSameContractAreRejectedEvenWhenNothingDependsOnIt()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(
            () => services.AddModules(Configuration, new StorageModule(), new OtherStorageModule()));

        Assert.Contains(typeof(StorageModule).FullName!, error.Message);
        Assert.Contains(typeof(OtherStorageModule).FullName!, error.Message);
        Assert.Contains(typeof(StorageContract).FullName!, error.Message);
        Assert.Empty(services);
    }

    [Fact]
    public void AContractAlreadyInTheCatalogCannotBeProvidedByAnotherModule()
    {
        var services = new ServiceCollection();
        services.AddModules(Configuration, new StorageModule());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new OtherStorageModule()));

        Assert.Contains(typeof(StorageModule).FullName!, error.Message);
        Assert.Contains(typeof(OtherStorageModule).FullName!, error.Message);
        Assert.Equal(["StorageModule"], Log(services).Modules);
    }

    [Fact]
    public void CallingAddModulesFromConfigureServicesIsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new ReentrantModule()));

        Assert.Contains("ConfigureServices", error.Message);
        Assert.Empty(Catalog(services).Modules);
    }

    [Fact]
    public void AThrowingConfigureServicesDoesNotLeaveTheCatalogLocked()
    {
        var services = new ServiceCollection();
        Assert.Throws<InvalidOperationException>(() => services.AddModules(Configuration, new ThrowingModule()));

        services.AddModules(Configuration, new StorageModule());

        Assert.Equal(["Storage"], Catalog(services).Modules.Select(module => module.Name));
    }

    [Fact]
    public void ModulesRegisterInDependencyOrderWhateverOrderTheyArePassedIn()
    {
        var services = new ServiceCollection();

        services.AddModules(Configuration, new BillingModule(), new OrdersModule(), new StorageModule());

        Assert.Equal(["StorageModule", "OrdersModule", "BillingModule"], Log(services).Modules);
        Assert.Equal(["Storage", "Orders", "Billing"], Catalog(services).Modules.Select(module => module.Name));
    }

    [Fact]
    public void RegisteringTheSameModulesAgainChangesNothing()
    {
        var services = new ServiceCollection();
        services.AddModules(Configuration, new StorageModule(), new OrdersModule());
        var count = services.Count;

        services.AddModules(Configuration, new StorageModule(), new OrdersModule());

        Assert.Equal(count, services.Count);
        Assert.Equal(["StorageModule", "OrdersModule"], Log(services).Modules);
    }

    [Fact]
    public void ALaterCallReusesTheModulesRegisteredEarlier()
    {
        var services = new ServiceCollection();
        services.AddModules(Configuration, new StorageModule());

        services.AddModules(Configuration, new OrdersModule());

        Assert.Equal(["StorageModule", "OrdersModule"], Log(services).Modules);
        Assert.Single(services, descriptor => descriptor.ServiceType == typeof(ModuleCatalog));
    }

    [Fact]
    public void SeparateServiceCollectionsDoNotShareRegistrations()
    {
        var first = new ServiceCollection();
        var second = new ServiceCollection();
        first.AddModules(Configuration, new StorageModule());

        var error = Assert.Throws<InvalidOperationException>(() => second.AddModules(Configuration, new OrdersModule()));

        Assert.Contains(typeof(StorageContract).FullName!, error.Message);
    }

    [Fact]
    public void AModuleComposesInIsolationWithOnlyItsDeclaredDependencies()
    {
        var services = new ServiceCollection();
        services.AddModules(Configuration, new OrdersModule(), new StorageModule());

        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        using var scope = provider.CreateScope();

        Assert.Same(provider.GetRequiredService<StorageService>(), scope.ServiceProvider.GetRequiredService<OrdersService>().Storage);
    }

    [Fact]
    public void AModulesControllersAreDiscovered()
    {
        var services = new ServiceCollection();
        var mvc = services.AddModules(Configuration, new StorageModule());

        var feature = new ControllerFeature();
        mvc.PartManager.PopulateFeature(feature);

        Assert.Single(mvc.PartManager.ApplicationParts.OfType<AssemblyPart>(), part => part.Assembly == typeof(StorageModule).Assembly);
        Assert.Contains(feature.Controllers, controller => controller.AsType() == typeof(OrdersController));
    }

    [Fact]
    public void TheCatalogNamesEachModuleWithItsAssemblyAndContract()
    {
        var services = new ServiceCollection();
        services.AddModules(Configuration, new StorageModule(), new OrdersModule());

        var orders = Catalog(services).Modules.Single(module => module.Name == "Orders");

        Assert.Equal(typeof(OrdersModule), orders.ModuleType);
        Assert.Equal(typeof(OrdersModule).Assembly, orders.Assembly);
        Assert.Equal(typeof(OrdersContract), orders.Contract);
        Assert.Null(orders.Description);
        Assert.Equal("Where everything is stored.", Catalog(services).Modules.Single(module => module.Name == "Storage").Description);
        using var provider = services.BuildServiceProvider();
        Assert.Same(Catalog(services), provider.GetRequiredService<ModuleCatalog>());
    }

    private static RegistrationLog Log(IServiceCollection services) =>
        (RegistrationLog)services.Single(descriptor => descriptor.ServiceType == typeof(RegistrationLog)).ImplementationInstance!;

    private static ModuleCatalog Catalog(IServiceCollection services) =>
        (ModuleCatalog)services.Single(descriptor => descriptor.ServiceType == typeof(ModuleCatalog)).ImplementationInstance!;
}
