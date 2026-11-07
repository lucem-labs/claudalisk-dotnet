using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

public sealed class DependencyValidationTests
{
    [Fact]
    public void AMissingDependencyIsRejectedWithTheModuleAndTheMarker()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new BillingModule(), new RefundsModule()));

        Assert.Contains("RefundsModule depends on PaymentsContract, but no module declares it as its contract.", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("BillingContract,", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnAmbiguousContractIsRejectedWithEveryModuleThatDeclaresIt()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new BillingModule(), new RivalBillingModule(), new OrderingModule()));

        Assert.Contains("BillingContract is ambiguous: it is the contract of BillingModule, RivalBillingModule.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AContractAlreadyRegisteredByAnotherModuleIsAmbiguous()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new BillingModule());

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new RivalBillingModule()));

        Assert.Contains("BillingContract is ambiguous: it is the contract of BillingModule, RivalBillingModule.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ACircularDependencyIsRejectedWithTheCycle()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new LoopStartModule(), new LoopEndModule()));

        Assert.Contains("They have a circular dependency: LoopStartModule -> LoopEndModule -> LoopStartModule.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AModuleThatDependsOnItsOwnContractIsCircular()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new EchoModule()));

        Assert.Contains("They have a circular dependency: EchoModule -> EchoModule.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryMissingAndAmbiguousDependencyIsReportedTogether()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new BillingModule(), new RivalBillingModule(), new RefundsModule()));

        Assert.Contains("BillingContract is ambiguous", error.Message, StringComparison.Ordinal);
        Assert.Contains("RefundsModule depends on PaymentsContract", error.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidSets() => ["missing", "ambiguous", "circular"];

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public void AnInvalidSetRegistersNothing(string problem)
    {
        var log = new RegistrationLog();
        IModule[] modules = problem switch
        {
            "missing" => [new BillingModule(log), new RefundsModule(log)],
            "ambiguous" => [new BillingModule(log), new OrderingModule(log), new RivalBillingModule(log)],
            _ => [new BillingModule(log), new LoopStartModule(log), new LoopEndModule(log)],
        };
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, modules));

        Assert.Empty(log.Modules);
        Assert.Empty(services);
    }

    [Fact]
    public void AFailedCallLeavesEarlierRegistrationsUntouched()
    {
        var services = new ServiceCollection().AddModules(Configurations.Empty, new BillingModule());
        var before = services.ToList();

        Assert.Throws<InvalidOperationException>(() => services.AddModules(Configurations.Empty, new RefundsModule()));

        Assert.Equal(before, services);
    }

    [Fact]
    public void ANullModuleIsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddModules(Configurations.Empty, new BillingModule(), null!));

        Assert.Equal("modules", error.ParamName);
        Assert.Empty(services);
    }

    [Fact]
    public void AModuleWithoutAContractIsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddModules(Configurations.Empty, new ContractlessModule()));

        Assert.StartsWith("ContractlessModule has no contract.", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ANullDependencyIsRejected()
    {
        var services = new ServiceCollection();

        var error = Assert.Throws<ArgumentException>(() => services.AddModules(Configurations.Empty, new NullDependencyModule()));

        Assert.StartsWith("NullDependencyModule lists null as a dependency.", error.Message, StringComparison.Ordinal);
    }

    private sealed class ContractlessModule : IModule
    {
        public Type Contract => null!;

        public IReadOnlyCollection<Type> Dependencies => [];

        public void Register(ModuleRegistration registration)
        {
        }
    }

    private sealed class NullDependencyModule : IModule
    {
        public Type Contract => typeof(RefundsContract);

        public IReadOnlyCollection<Type> Dependencies => [null!];

        public void Register(ModuleRegistration registration)
        {
        }
    }
}
