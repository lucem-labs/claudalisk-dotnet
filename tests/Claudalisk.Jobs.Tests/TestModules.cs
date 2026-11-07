using Claudalisk.Dispatching;
using Claudalisk.Jobs.PostgreSql;
using Claudalisk.Domain;
using Claudalisk.Modules;
using Claudalisk.Persistence;
using FluentValidation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Jobs.Tests;

public sealed class OrdersContract;

public sealed class BillingContract;

public sealed class MailContract;

public sealed record PlaceOrderCommand(Guid Id, string Reference, bool FailAfterQueueing);

public sealed record ShipOrderCommand(Guid OrderId);

public sealed record FlakyCommand(int FailedAttempts);

public sealed record StrictCommand(string Reference);

public sealed record RefusedCommand(string Reference);

public sealed record OutageCommand(int WaitMinutes);

public sealed class OrderRefused() : DomainException("order-refused", "The order is refused.", DomainErrorKind.Conflict);

public sealed record WaitCommand(Guid Key, double Progress);

public sealed record CancelOrderCommand(Guid OrderId, bool FailAfterCancel);

public sealed record OrderCanceled(Guid OrderId);

internal sealed record ImportOrdersCommand(int Total, int CrashWhenDone);

internal sealed record ImportCheckpoint(int Done);

internal sealed record OrderWasCanceled(Guid OrderId) : IDomainEvent;

internal sealed class OrderRow
{
    public Guid Id { get; set; }

    public required string Reference { get; set; }

    public bool Shipped { get; set; }

    public bool Canceled { get; set; }
}

internal sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public DbSet<OrderRow> Orders => Set<OrderRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orders");
        modelBuilder.Entity<OrderRow>(order => order.ToTable("orders"));
    }
}

internal sealed class RefundRow
{
    public Guid Id { get; set; }

    public Guid OrderId { get; set; }
}

internal sealed class BillingDbContext(DbContextOptions<BillingDbContext> options) : DbContext(options)
{
    public DbSet<RefundRow> Refunds => Set<RefundRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("billing");
        modelBuilder.Entity<RefundRow>(refund => refund.ToTable("refunds"));
    }
}

internal sealed class MailDbContext(DbContextOptions<MailDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("mail");
}

internal sealed class Order(Guid id, long version) : AggregateRoot(version)
{
    public Guid Id { get; } = id;

    public void Cancel() => Record(new OrderWasCanceled(Id));
}

public sealed class Signals
{
    private readonly Dictionary<Guid, (TaskCompletionSource Started, TaskCompletionSource Release)> _waits = [];

    public (TaskCompletionSource Started, TaskCompletionSource Release) For(Guid key)
    {
        lock (_waits)
        {
            if (!_waits.TryGetValue(key, out var wait))
            {
                wait = (new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously), new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
                _waits[key] = wait;
            }

            return wait;
        }
    }
}

internal sealed class PlaceOrderHandler(OrdersDbContext db, IJobQueue jobs) : IHandler<PlaceOrderCommand, JobReference>
{
    public async Task<JobReference> HandleAsync(PlaceOrderCommand message, CancellationToken cancellationToken)
    {
        db.Orders.Add(new OrderRow { Id = message.Id, Reference = message.Reference });
        await db.SaveChangesAsync(cancellationToken);
        var job = await jobs.EnqueueAsync(new ShipOrderCommand(message.Id), cancellationToken: cancellationToken);
        if (message.FailAfterQueueing)
        {
            throw new InvalidOperationException("Placing the order failed after it queued its job.");
        }

        return job;
    }
}

internal sealed class ShipOrderHandler(OrdersDbContext db, IJobContext job) : IHandler<ShipOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(ShipOrderCommand message, CancellationToken cancellationToken)
    {
        var order = await db.Orders.SingleAsync(row => row.Id == message.OrderId, cancellationToken);
        order.Shipped = true;
        await db.SaveChangesAsync(cancellationToken);
        job.SetResult($"/api/orders/{order.Id}");
        return order.Id;
    }
}

internal sealed class FlakyHandler(IJobContext job) : IHandler<FlakyCommand, int>
{
    public Task<int> HandleAsync(FlakyCommand message, CancellationToken cancellationToken) =>
        job.Attempt <= message.FailedAttempts
            ? throw new InvalidOperationException($"Attempt {job.Attempt} failed.")
            : Task.FromResult(job.Attempt);
}

internal sealed class StrictHandler : IHandler<StrictCommand, int>
{
    public Task<int> HandleAsync(StrictCommand message, CancellationToken cancellationToken) => Task.FromResult(1);
}

internal sealed class RefusedHandler : IHandler<RefusedCommand, int>
{
    public Task<int> HandleAsync(RefusedCommand message, CancellationToken cancellationToken) => throw new OrderRefused();
}

internal sealed class OutageHandler : IHandler<OutageCommand, int>
{
    public Task<int> HandleAsync(OutageCommand message, CancellationToken cancellationToken) =>
        throw new DependencyUnavailableException(
            "The carrier's API is down.",
            message.WaitMinutes > 0 ? TimeSpan.FromMinutes(message.WaitMinutes) : null);
}

internal sealed class StrictCommandValidator : AbstractValidator<StrictCommand>
{
    public StrictCommandValidator() => RuleFor(command => command.Reference).NotEmpty();
}

internal sealed class WaitHandler(Signals signals, IJobContext job) : IHandler<WaitCommand, int>
{
    public async Task<int> HandleAsync(WaitCommand message, CancellationToken cancellationToken)
    {
        await job.ReportProgressAsync(message.Progress, "waiting", cancellationToken);
        var (started, release) = signals.For(message.Key);
        started.TrySetResult();
        await release.Task.WaitAsync(cancellationToken);
        return 1;
    }
}

internal sealed class CancelOrderHandler(OrdersDbContext db, IAggregateTracker tracker) : IHandler<CancelOrderCommand, Guid>
{
    public async Task<Guid> HandleAsync(CancelOrderCommand message, CancellationToken cancellationToken)
    {
        var row = await db.Orders.SingleAsync(order => order.Id == message.OrderId, cancellationToken);
        var order = new Order(row.Id, 1);
        order.Cancel();
        row.Canceled = true;
        await db.SaveChangesAsync(cancellationToken);
        tracker.Track(order);
        if (message.FailAfterCancel)
        {
            throw new InvalidOperationException("Canceling failed after the aggregate changed.");
        }

        return row.Id;
    }
}

internal sealed class ImportOrdersHandler(OrdersDbContext db, IJobContext job) : IBatchJobHandler<ImportOrdersCommand, ImportCheckpoint>
{
    public const int BatchSize = 2;

    public async Task<BatchStep<ImportCheckpoint>> RunBatchAsync(ImportOrdersCommand command, ImportCheckpoint? checkpoint, CancellationToken cancellationToken)
    {
        var done = checkpoint?.Done ?? 0;
        if (job.Attempt == 1 && done == command.CrashWhenDone)
        {
            throw new InvalidOperationException($"The import crashed after {done} orders.");
        }

        var next = Math.Min(command.Total, done + BatchSize);
        for (var number = done + 1; number <= next; number++)
        {
            db.Orders.Add(new OrderRow { Id = Guid.NewGuid(), Reference = $"import-{job.JobId:N}-{number}" });
        }

        await db.SaveChangesAsync(cancellationToken);
        if (next == command.Total)
        {
            job.SetResult("/api/orders");
        }

        return new BatchStep<ImportCheckpoint>(new ImportCheckpoint(next), next == command.Total, (double)next / command.Total);
    }
}

internal sealed class RefundOnOrderCanceledHandler(BillingDbContext db) : IEventHandler<OrderCanceled>
{
    public async Task HandleAsync(OrderCanceled integrationEvent, CancellationToken cancellationToken)
    {
        db.Refunds.Add(new RefundRow { Id = Guid.NewGuid(), OrderId = integrationEvent.OrderId });
        await db.SaveChangesAsync(cancellationToken);
    }
}

internal sealed class MailOnOrderCanceledHandler : IEventHandler<OrderCanceled>
{
    public Task HandleAsync(OrderCanceled integrationEvent, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("The mail server is down.");
}

public sealed class OrdersModule : IModule
{
    public Type Contract => typeof(OrdersContract);

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<OrdersDbContext>("orders");
        services.AddModuleJobs("Orders", "orders", jobs => jobs
            .Lane("slow", 1)
            .Command<ShipOrderCommand>("ship-order")
            .Command<FlakyCommand>("flaky")
            .Command<StrictCommand>("strict")
            .Command<RefusedCommand>("refused")
            .Command<OutageCommand>("outage")
            .Command<WaitCommand>("wait")
            .Batch<ImportOrdersCommand, ImportCheckpoint, ImportOrdersHandler>("import-orders")
            .MapEvent<OrderWasCanceled>(canceled => new OrderCanceled(canceled.OrderId)));
    }
}

public sealed class BillingModule : IModule
{
    public Type Contract => typeof(BillingContract);

    public IReadOnlyCollection<Type> Dependencies => [typeof(OrdersContract)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<BillingDbContext>("billing");
        services.AddModuleJobs("Billing", "billing", jobs => jobs.Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("refund-on-order-canceled"));
    }
}

public sealed class MailModule : IModule
{
    public Type Contract => typeof(MailContract);

    public IReadOnlyCollection<Type> Dependencies => [typeof(OrdersContract)];

    public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
    {
        services.AddModuleDbContext<MailDbContext>("mail");
        services.AddModuleJobs("Mail", "mail", jobs => jobs.Subscribe<OrderCanceled, MailOnOrderCanceledHandler>("mail-on-order-canceled"));
    }
}

internal static class TestApp
{
    public static IServiceCollection AddTestApp(this IServiceCollection services, string connectionString)
    {
        services.AddLogging();
        services.AddPersistence(connectionString);
        services.AddModules(new ConfigurationBuilder().Build(), new OrdersModule(), new BillingModule(), new MailModule());
        services.AddDispatching();
        services.AddJobs(options =>
        {
            options.WorkersEnabled = false;
            options.RetryBaseDelay = TimeSpan.Zero;
            options.HeartbeatInterval = TimeSpan.FromMilliseconds(20);
        });
        services.AddPostgreSqlJobs();
        services.AddSingleton<Signals>();
        return services;
    }
}
