using Claudalisk.Domain;
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
    public int Order => PipelineOrder.UnitOfWork + 100;

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
        services.AddSingleton<AttemptLog>();
        services.AddSingleton<Gate>();
        services.AddSingleton<Background>();
        services.AddScoped<ThrowingDisposable>();
        services.AddSingleton<StepTrace>();
        return services;
    }
}

public sealed class AttemptLog
{
    public List<Guid> ScopeIds { get; } = [];

    public bool ThrowOnDispose { get; set; }
}

public sealed class Gate
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private int _entries;

    public int Entries => _entries;

    public void Enter() => Interlocked.Increment(ref _entries);
}

public sealed class StepTrace
{
    public List<(string Step, bool UnitOfWorkActive)> Entries { get; } = [];
}

internal sealed class FlakyHandler(OrdersDbContext db, AttemptLog log, ScopeMarker marker, ThrowingDisposable disposable) : IHandler<FlakyCommand, Guid>
{
    public async Task<Guid> HandleAsync(FlakyCommand message, CancellationToken cancellationToken)
    {
        log.ScopeIds.Add(marker.Id);
        GC.KeepAlive(disposable);
        db.Orders.Add(new Order { Id = Guid.NewGuid(), Reference = "flaky" });
        await db.SaveChangesAsync(cancellationToken);
        if (log.ScopeIds.Count <= message.FailAttempts)
        {
            throw new ConcurrencyConflictException("Lost the race.");
        }

        return message.Id;
    }
}

internal sealed class RelayHandler(IDispatcher dispatcher) : IHandler<RelayCommand, Guid>
{
    public Task<Guid> HandleAsync(RelayCommand message, CancellationToken cancellationToken) =>
        dispatcher.SendAsync<Guid>(new FlakyCommand(message.Id, int.MaxValue), cancellationToken);
}

internal sealed class StaleHandler(AttemptLog log, ScopeMarker marker) : IHandler<StaleCommand, int>
{
    public Task<int> HandleAsync(StaleCommand message, CancellationToken cancellationToken)
    {
        log.ScopeIds.Add(marker.Id);
        throw new StaleVersionException(2, 3);
    }
}

internal sealed class ConflictingHandler(AttemptLog log, ScopeMarker marker) : IHandler<ConflictingQuery, int>
{
    public Task<int> HandleAsync(ConflictingQuery message, CancellationToken cancellationToken)
    {
        log.ScopeIds.Add(marker.Id);
        throw new ConcurrencyConflictException("Lost the race.");
    }
}

internal sealed class GateHandler(Gate gate) : IHandler<GateQuery, int>
{
    public async Task<int> HandleAsync(GateQuery message, CancellationToken cancellationToken)
    {
        gate.Enter();
        gate.Entered.TrySetResult();
        await gate.Release.Task.WaitAsync(cancellationToken);
        return 1;
    }
}

internal sealed class FanOutHandler(IDispatcher dispatcher, Gate gate) : IHandler<FanOutQuery, int>
{
    public async Task<int> HandleAsync(FanOutQuery message, CancellationToken cancellationToken)
    {
        var first = dispatcher.SendAsync<int>(new GateQuery(), cancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        var second = dispatcher.SendAsync<int>(new CountOrdersQuery(), cancellationToken);
        gate.Release.TrySetResult();
        return (await Task.WhenAll(first, second)).Sum();
    }
}

internal sealed class StructHandler : IHandler<StructCommand, Guid>
{
    public Task<Guid> HandleAsync(StructCommand message, CancellationToken cancellationToken) => Task.FromResult(message.Id);
}

internal sealed class NestedHandler : IHandler<NestedMessages.NestedQuery, int>
{
    public Task<int> HandleAsync(NestedMessages.NestedQuery message, CancellationToken cancellationToken) => Task.FromResult(7);
}

internal sealed class DerivedHandler : IHandler<DerivedCommand, Guid>
{
    public Task<Guid> HandleAsync(DerivedCommand message, CancellationToken cancellationToken) => Task.FromResult(Guid.Empty);
}

internal sealed class BaseCommandValidator : AbstractValidator<BaseCommand>
{
    public BaseCommandValidator() => RuleFor(command => command.Name).NotEmpty();
}

internal sealed class NamedMessageValidator : AbstractValidator<INamedMessage>
{
    public NamedMessageValidator() => RuleFor(message => message.Name).NotEmpty();
}

internal sealed class InvoiceIssuedEventValidator : AbstractValidator<InvoiceIssuedEvent>
{
    public InvoiceIssuedEventValidator() => RuleFor(issued => issued.Number).NotEmpty();
}

internal abstract class TracingStep(StepTrace trace, IUnitOfWork unitOfWork) : IPipelineStep
{
    public abstract int Order { get; }

    public Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        trace.Entries.Add((GetType().Name, unitOfWork.IsActive));
        return next(cancellationToken);
    }
}

internal sealed class AfterLoggingStep(StepTrace trace, IUnitOfWork unitOfWork) : TracingStep(trace, unitOfWork)
{
    public override int Order => PipelineOrder.Logging + 500;
}

internal sealed class AfterRetryStep(StepTrace trace, IUnitOfWork unitOfWork) : TracingStep(trace, unitOfWork)
{
    public override int Order => PipelineOrder.Retry + 500;
}

internal sealed class AfterUnitOfWorkStep(StepTrace trace, IUnitOfWork unitOfWork) : TracingStep(trace, unitOfWork)
{
    public override int Order => PipelineOrder.UnitOfWork + 500;
}

internal sealed class RetrySlotStep : IPipelineStep
{
    public int Order => PipelineOrder.Retry;

    public Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken) => next(cancellationToken);
}

public sealed class Background
{
    public Task<int>? Task { get; set; }

    public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
}

public sealed class ThrowingDisposable(AttemptLog log) : IAsyncDisposable
{
    public ValueTask DisposeAsync() => log.ThrowOnDispose ? throw new InvalidOperationException("The scoped service could not be disposed.") : ValueTask.CompletedTask;
}

internal sealed class AbandonHandler(IDispatcher dispatcher, Gate gate, Background background) : IHandler<AbandonQuery, int>
{
    public async Task<int> HandleAsync(AbandonQuery message, CancellationToken cancellationToken)
    {
        background.Task = dispatcher.SendAsync<int>(new GateQuery(), cancellationToken);
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        throw new InvalidOperationException("The handler gave up before its nested send finished.");
    }
}

internal sealed class BackgroundHandler(IDispatcher dispatcher, Background background) : IHandler<BackgroundQuery, int>
{
    public Task<int> HandleAsync(BackgroundQuery message, CancellationToken cancellationToken)
    {
        background.Task = Task.Run(
            async () =>
            {
                await background.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30));
                return await dispatcher.SendAsync<int>(new CountOrdersQuery());
            },
            CancellationToken.None);
        return Task.FromResult(0);
    }
}
