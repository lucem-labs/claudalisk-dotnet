using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules.Tests;

internal sealed class BillingContract;

internal sealed class OrderingContract;

internal sealed class ShippingContract;

internal sealed class PaymentsContract;

internal sealed class RefundsContract;

internal sealed class LoopStartContract;

internal sealed class LoopEndContract;

internal sealed class EchoContract;

internal sealed class BillingLedger;

internal sealed class OrderBook(BillingLedger ledger)
{
    public BillingLedger Ledger { get; } = ledger;
}

internal sealed class ShippingQueue(OrderBook orders)
{
    public OrderBook Orders { get; } = orders;
}

internal sealed class RegistrationLog
{
    public List<Type> Modules { get; } = [];
}

internal abstract class ExampleModule(RegistrationLog? log, Type contract, params Type[] dependencies) : IModule
{
    public RegistrationLog Log { get; } = log ?? new RegistrationLog();

    public ModuleRegistration? Registration { get; private set; }

    public Type Contract => contract;

    public IReadOnlyCollection<Type> Dependencies => dependencies;

    public void Register(ModuleRegistration registration)
    {
        Log.Modules.Add(GetType());
        Registration = registration;
        RegisterServices(registration.Services, registration.Configuration);
    }

    protected virtual void RegisterServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}

internal sealed class BillingModule(RegistrationLog? log = null) : ExampleModule(log, typeof(BillingContract))
{
    protected override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<BillingLedger>();
}

internal sealed class RivalBillingModule(RegistrationLog? log = null) : ExampleModule(log, typeof(BillingContract));

internal sealed class OrderingModule(RegistrationLog? log = null) : ExampleModule(log, typeof(OrderingContract), typeof(BillingContract))
{
    protected override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<OrderBook>();
}

internal sealed class ShippingModule(RegistrationLog? log = null) : ExampleModule(log, typeof(ShippingContract), typeof(OrderingContract), typeof(BillingContract))
{
    protected override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddSingleton<ShippingQueue>();
}

internal sealed class RefundsModule(RegistrationLog? log = null) : ExampleModule(log, typeof(RefundsContract), typeof(PaymentsContract), typeof(BillingContract));

internal sealed class LoopStartModule(RegistrationLog? log = null) : ExampleModule(log, typeof(LoopStartContract), typeof(LoopEndContract));

internal sealed class LoopEndModule(RegistrationLog? log = null) : ExampleModule(log, typeof(LoopEndContract), typeof(LoopStartContract));

internal sealed class EchoModule(RegistrationLog? log = null) : ExampleModule(log, typeof(EchoContract), typeof(EchoContract));

internal sealed class ConfiguredModule(RegistrationLog? log = null) : ExampleModule(log, typeof(PaymentsContract))
{
    public string? Currency { get; private set; }

    protected override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        Currency = configuration["Payments:Currency"];
}

internal sealed class FailingModule(RegistrationLog? log = null) : ExampleModule(log, typeof(RefundsContract))
{
    protected override void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        throw new InvalidOperationException("Refunds needs a payment provider.");
}

internal static class Configurations
{
    public static IConfiguration Empty { get; } = new ConfigurationBuilder().Build();
}
