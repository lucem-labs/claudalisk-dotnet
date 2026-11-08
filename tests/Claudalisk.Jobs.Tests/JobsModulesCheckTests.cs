using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Claudalisk.Jobs.Tests;

public sealed class JobsModulesCheckTests
{
    private static Task StartAsync(Action<IServiceCollection> configure)
    {
        var services = new ServiceCollection().AddTestApp("Host=localhost");
        configure(services);
        var provider = services.BuildServiceProvider();
        return provider.GetServices<IHostedService>().Single(service => service.GetType().Name == "JobsModulesCheck").StartAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task TheJobsOfEveryModuleInTheCatalogPassTheStartupCheck()
    {
        await StartAsync(_ => { });
    }

    [Fact]
    public async Task JobsNamedAfterNoModuleFailStartupNamingThemAndTheKnownModules()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => StartAsync(services => services.AddModuleJobs("Ordres", "ordres", _ => { })));

        Assert.Contains("'Ordres'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("known modules: 'Billing', 'Mail', 'Orders'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task JobsOfALibraryOwnedModuleNeedNoModuleInTheCatalog()
    {
        await StartAsync(services => services.AddModuleJobs("Scheduling", "scheduling", jobs => jobs.LibraryOwned()));
    }
}
