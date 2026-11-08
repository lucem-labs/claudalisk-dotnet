using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Claudalisk.Dispatching.Tests;

public sealed class RegistrationTests
{
    private const string UnusedDatabase = "Host=localhost;Database=unused";

    [Fact]
    public void TheHandlersAndValidatorsOfEveryModuleAreFound()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);

        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHandler<PlaceOrderCommand, Guid>) && descriptor.ImplementationType == typeof(PlaceOrderHandler));
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(IHandler<CheckOutCommand, Guid>) && descriptor.Lifetime == ServiceLifetime.Scoped);
        Assert.Contains(services, descriptor => descriptor.ServiceType == typeof(FluentValidation.IValidator<PlaceOrderCommand>));
        using var provider = services.BuildServiceProvider();
        provider.ValidateDispatching();
    }

    [Fact]
    public void AddingDispatchingAgainRegistersNothingNew()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        var count = services.Count;

        services.AddClaudaliskDispatching();

        Assert.Equal(count, services.Count);
    }

    [Fact]
    public void AMessageOfAnInterfacesAssemblyWithoutAHandlerFailsAtStartup()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<ChargeOrderCommand, Guid>)));

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(ChargeOrderCommand).FullName}' has no handler", error.Message);
    }

    [Fact]
    public void AMessageWithTwoHandlersFailsAtStartup()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.AddScoped<IHandler<PlaceOrderCommand, Guid>, DuplicatePlaceOrderHandler<int>>();

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(PlaceOrderCommand).FullName}' has 2 handlers", error.Message);
        Assert.Contains(typeof(PlaceOrderHandler).FullName!, error.Message);
    }

    [Fact]
    public void ASingletonHandlerFailsAtStartup()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        var scoped = services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<CountOrdersQuery, int>));
        services.Remove(scoped);
        services.AddSingleton(scoped.ServiceType, scoped.ImplementationType!);

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(CountOrdersHandler).FullName}' is a singleton", error.Message);
    }

    [Fact]
    public async Task TheHostRunsTheStartupCheck()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<ChargeOrderCommand, Guid>)));
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var hosted in provider.GetServices<IHostedService>())
            {
                await hosted.StartAsync(TestContext.Current.CancellationToken);
            }
        });

        Assert.Contains("has no handler", error.Message);
    }

    [Fact]
    public async Task WithoutDockerTheDatabaseTestsFailAndSayDockerIsNeeded()
    {
        var unreachable = new InvalidOperationException("Docker is either not running or misconfigured.");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresServer.StartOrExplainAsync(() => Task.FromException(unreachable)));

        Assert.StartsWith("The database tests need Docker", failure.Message);
    }

    [Fact]
    public void TheAbstractionsReferenceOnlyTheBaseLibrary()
    {
        var references = typeof(IDispatcher).Assembly.GetReferencedAssemblies().Select(reference => reference.Name!).ToArray();

        Assert.All(references, name => Assert.True(
            name is "System" or "netstandard" || name.StartsWith("System.", StringComparison.Ordinal),
            $"Claudalisk.Dispatching.Abstractions references {name}."));
    }
}
