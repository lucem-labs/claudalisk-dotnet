using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching.Tests;

public sealed class StartupCheckTests
{
    private const string UnusedDatabase = "Host=localhost;Database=unused";

    [Fact]
    public void AKeyedHandlerFailsAtStartupAndIsNotCountedAsADuplicate()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.AddKeyedScoped<IHandler<PlaceOrderCommand, Guid>, DuplicatePlaceOrderHandler<string>>("special");

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(DuplicatePlaceOrderHandler<string>).FullName}' is registered as a keyed IHandler", error.Message);
        Assert.DoesNotContain("handlers:", error.Message);
        Assert.DoesNotContain("has no handler", error.Message);
    }

    [Fact]
    public void AHandlerForAMessageNamedNeitherCommandNorQueryFailsAtStartup()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.AddScoped<IHandler<ShipmentSent, int>, ShipmentSentHandler<int>>();

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(ShipmentSent).FullName}', whose name ends in neither Command nor Query", error.Message);
    }

    [Fact]
    public void StructAndNestedMessagesNeedAHandlerToo()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<StructCommand, Guid>)));
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<NestedMessages.NestedQuery, int>)));

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains($"'{typeof(StructCommand).FullName}' has no handler", error.Message);
        Assert.Contains($"'{typeof(NestedMessages.NestedQuery).FullName}' has no handler", error.Message);
        Assert.DoesNotContain(nameof(ReportKindQuery), error.Message);
        Assert.DoesNotContain(nameof(RunCommand), error.Message);
        Assert.DoesNotContain(nameof(IAuditQuery), error.Message);
        Assert.DoesNotContain(nameof(BaseCommand) + "'", error.Message);
        Assert.DoesNotContain("GenericCommand", error.Message);
    }

    [Fact]
    public void AnAssemblyWhoseTypesCannotBeListedBecomesAProblemNamingTheAssemblyAndTheLoaderMessages()
    {
        var problems = new List<string>();

        var messages = DispatchRegistry.ContractMessages([new BrokenAssembly(new ReflectionTypeLoadException([null], [new FileNotFoundException("Missing.Dependency.dll was not found.")]))], problems);

        Assert.Empty(messages);
        var problem = Assert.Single(problems);
        Assert.Contains("assembly 'Broken.Contracts' could not be loaded", problem);
        Assert.Contains("Missing.Dependency.dll was not found.", problem);
    }

    [Fact]
    public void AnAssemblyThatThrowsWhileListingBecomesAProblemInsteadOfACrash()
    {
        var problems = new List<string>();

        DispatchRegistry.ContractMessages([new BrokenAssembly(new FileNotFoundException("The file could not be found."))], problems);

        Assert.Contains("The file could not be found.", Assert.Single(problems));
    }

    [Fact]
    public async Task TheHostStartupCheckRejectsAStepWithTheRetryOrder()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.AddScoped<IPipelineStep, RetrySlotStep>();
        await using var provider = services.BuildServiceProvider();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            foreach (var hosted in provider.GetServices<Microsoft.Extensions.Hosting.IHostedService>())
            {
                await hosted.StartAsync(TestContext.Current.CancellationToken);
            }
        });

        Assert.Contains("belongs to the dispatcher", error.Message);
    }

    [Fact]
    public void AllProblemsAreReportedTogether()
    {
        var services = new ServiceCollection().AddTestApp(UnusedDatabase);
        services.Remove(services.Single(descriptor => descriptor.ServiceType == typeof(IHandler<ChargeOrderCommand, Guid>)));
        services.AddScoped<IHandler<ShipmentSent, int>, ShipmentSentHandler<int>>();

        var error = Assert.Throws<InvalidOperationException>(() => services.BuildServiceProvider().ValidateDispatching());

        Assert.Contains("has no handler", error.Message);
        Assert.Contains("neither Command nor Query", error.Message);
    }

    private sealed record ShipmentSent;

    private sealed class ShipmentSentHandler<TMarker> : IHandler<ShipmentSent, int>
    {
        public ValueTask<int> HandleAsync(ShipmentSent message, CancellationToken cancellationToken) => ValueTask.FromResult(0);
    }

    private sealed class BrokenAssembly(Exception failure) : Assembly
    {
        public override Type[] GetExportedTypes() => throw failure;

        public override AssemblyName GetName() => new("Broken.Contracts");
    }
}
