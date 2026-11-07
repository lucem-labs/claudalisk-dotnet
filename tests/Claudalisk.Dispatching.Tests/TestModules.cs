using Claudalisk.Modules;
using Claudalisk.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching.Tests;

internal sealed class Order
{
    public Guid Id { get; set; }

    public required string Reference { get; set; }
}

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string Schema = "orders";

    public DbSet<Order> Orders => Set<Order>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Order>(order => order.ToTable("orders"));
    }
}

internal sealed class Charge
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }

    public decimal Amount { get; set; }
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public const string Schema = "billing";

    public DbSet<Charge> Charges => Set<Charge>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        modelBuilder.Entity<Charge>(charge => charge.ToTable("charges"));
    }
}

public sealed class ScopeMarker
{
    public Guid Id { get; } = Guid.NewGuid();
}

public sealed class SendLog
{
    public List<string> Sends { get; } = [];
}

internal sealed class RecordingStep(SendLog log) : IPipelineStep
{
    public Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        log.Sends.Add(context.MessageType.Name);
        return next(cancellationToken);
    }
}

internal sealed class PlaceOrderHandler(OrdersDbContext db) : IHandler<PlaceOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(PlaceOrderCommand message, CancellationToken cancellationToken)
    {
        db.Orders.Add(new Order { Id = message.Id, Reference = message.Reference });
        await db.SaveChangesAsync(cancellationToken);
        return message.Id;
    }
}

internal sealed class PlaceOrderCommandValidator : AbstractValidator<PlaceOrderCommand>
{
    public PlaceOrderCommandValidator() => RuleFor(command => command.Reference).NotEmpty().MaximumLength(10);
}

internal sealed class CountOrdersHandler(OrdersDbContext db) : IHandler<CountOrdersQuery, int>
{
    public Task<int> HandleAsync(CountOrdersQuery message, CancellationToken cancellationToken) => db.Orders.CountAsync(cancellationToken);
}

internal sealed class PeekOrdersHandler(OrdersDbContext db) : IHandler<PeekOrdersQuery, int>
{
    public async Task<int> HandleAsync(PeekOrdersQuery message, CancellationToken cancellationToken)
    {
        db.Orders.Add(new Order { Id = message.PlaceFirst, Reference = "peek" });
        await db.SaveChangesAsync(cancellationToken);
        throw new InvalidOperationException("The query failed after it wrote.");
    }
}

internal sealed class WhichScopeHandler(ScopeMarker marker) : IHandler<WhichScopeQuery, Guid>
{
    public Task<Guid> HandleAsync(WhichScopeQuery message, CancellationToken cancellationToken) => Task.FromResult(marker.Id);
}

internal sealed class ChargeOrderHandler(BillingDbContext db) : IHandler<ChargeOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(ChargeOrderCommand message, CancellationToken cancellationToken)
    {
        var charge = new Charge { Id = Guid.NewGuid(), OrderId = message.OrderId, Amount = message.Amount };
        db.Charges.Add(charge);
        await db.SaveChangesAsync(cancellationToken);
        return charge.Id;
    }
}

internal sealed class CountChargesHandler(BillingDbContext db) : IHandler<CountChargesQuery, int>
{
    public Task<int> HandleAsync(CountChargesQuery message, CancellationToken cancellationToken) => db.Charges.CountAsync(cancellationToken);
}

internal sealed class CheckOutHandler(IDispatcher dispatcher) : IHandler<CheckOutCommand, Guid>
{
    public async Task<Guid> HandleAsync(CheckOutCommand message, CancellationToken cancellationToken)
    {
        await dispatcher.SendAsync<Guid>(new PlaceOrderCommand(message.OrderId, message.Reference), cancellationToken);
        var charge = await dispatcher.SendAsync<Guid>(new ChargeOrderCommand(message.OrderId, message.Amount), cancellationToken);
        if (message.FailAfterBoth)
        {
            throw new InvalidOperationException("Checkout failed after both modules saved.");
        }

        return charge;
    }
}

internal sealed class DuplicatePlaceOrderHandler<TMarker> : IHandler<PlaceOrderCommand, Guid>
{
    public Task<Guid> HandleAsync(PlaceOrderCommand message, CancellationToken cancellationToken) => Task.FromResult(Guid.Empty);
}

public sealed class OrdersModule : IModule
{
    public Type Contract => typeof(OrdersContract);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrdersDbContext>(OrdersDbContext.Schema);
        services.AddScoped<ScopeMarker>();
    }
}

public sealed class BillingModule : IModule
{
    public Type Contract => typeof(BillingContract);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddModuleDbContext<BillingDbContext>(BillingDbContext.Schema);
}

public sealed class CheckoutModule : IModule
{
    public Type Contract => typeof(CheckoutContract);

    public IReadOnlyCollection<Type> Dependencies => [typeof(OrdersContract), typeof(BillingContract)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
    }
}

internal static class TestApp
{
    public static IServiceCollection AddTestApp(this IServiceCollection services, string connectionString)
    {
        services.AddLogging();
        services.AddPersistence(connectionString);
        services.AddModules(new ConfigurationBuilder().Build(), new OrdersModule(), new BillingModule(), new CheckoutModule());
        services.AddDispatching();
        services.AddSingleton<SendLog>();
        services.AddScoped<IPipelineStep, RecordingStep>();
        return services;
    }
}
