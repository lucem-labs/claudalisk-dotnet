using System.Net;
using Claudalisk.Modules.Tests.Catalog;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class ControllerDiscoveryTests
{
    [Fact]
    public async Task AModulesControllerAnswersInTheHost()
    {
        await using var app = TestHosts.Build(new CatalogModule());
        using var client = await TestHosts.StartAsync(app);

        using var response = await client.GetAsync(new Uri("api/catalog/prices/tea", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("tea", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AHostWithoutTheModuleDoesNotRouteToItsController()
    {
        await using var app = TestHosts.Build(builder => builder.Services.AddControllers());
        using var client = await TestHosts.StartAsync(app);

        using var response = await client.GetAsync(new Uri("api/catalog/prices/tea", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void AModulesControllersAreAddedAsAnApplicationPart()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new CatalogModule());

        Assert.Contains(typeof(GetPriceController), Controllers(services));
    }

    [Fact]
    public void AModuleAssemblyIsAddedAsOnePartAcrossRepeatedCalls()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new CatalogModule());

        services.AddModules(Configurations.Empty, new CatalogModule(), new BillingModule());
        services.AddModules(Configurations.Empty, new OrderingModule());

        var parts = PartManager(services).ApplicationParts.OfType<AssemblyPart>().ToList();
        Assert.Single(parts, part => part.Assembly == typeof(CatalogModule).Assembly);
        Assert.Single(parts, part => part.Assembly == typeof(BillingModule).Assembly);
        Assert.Single(Controllers(services), controller => controller == typeof(GetPriceController));
    }

    [Fact]
    public void AnAssemblyTheHostAlreadyAddedIsNotAddedAgain()
    {
        var services = new ServiceCollection();
        services.AddControllers().AddApplicationPart(typeof(CatalogModule).Assembly);

        services.AddModules(Configurations.Empty, new CatalogModule());

        Assert.Single(PartManager(services).ApplicationParts.OfType<AssemblyPart>(), part => part.Assembly == typeof(CatalogModule).Assembly);
    }

    private static ApplicationPartManager PartManager(IServiceCollection services) =>
        services.Select(descriptor => descriptor.ImplementationInstance).OfType<ApplicationPartManager>().Single();

    private static List<Type> Controllers(IServiceCollection services)
    {
        var feature = new ControllerFeature();
        PartManager(services).PopulateFeature(feature);
        return feature.Controllers.Select(controller => controller.AsType()).ToList();
    }
}
