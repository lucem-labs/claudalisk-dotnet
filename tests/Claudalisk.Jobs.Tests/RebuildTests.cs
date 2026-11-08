using Claudalisk.Dispatching;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Claudalisk.Jobs.Tests.JobsTests;

namespace Claudalisk.Jobs.Tests;

public sealed class RebuildTests
{
    [Fact]
    public async Task ARebuildRecomputesTheReadModelAndSettlesTheQueuedAndRunningDeliveriesItsSnapshotIncludes()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var first = await CanceledOrderAsync(app);
        var second = await CanceledOrderAsync(app);
        var billing = Registry(app).Find("Billing")!;
        var running = await app.GetRequiredService<IJobTransport>().ClaimAsync(billing, "refunds", TestContext.Current.CancellationToken);
        var unseen = Guid.NewGuid();
        await PublishAsync(app, new OrderCanceled(unseen));
        await ScalarAsync(app, "UPDATE billing.jobs SET not_before = now() + interval '1 hour' WHERE status = 'queued'");
        var rebuild = await EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid()));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));

        var rebuilt = (await JobAsync(app, "Billing", rebuild.Id))!;
        Assert.Equal((JobStatus.Succeeded, "refunds"), (rebuilt.Status, rebuilt.Lane));
        Assert.Equal<(Guid, string)>(
            [(first, "succeeded"), (second, "succeeded"), (unseen, "queued")],
            await DeliveriesAsync(app));
        Assert.Equal(first, await ScalarAsync(app, $"SELECT (payload->>'orderId')::uuid FROM billing.jobs WHERE id = '{running!.Id}'"));
        Assert.Equal(2L, await ScalarAsync(app, "SELECT count(*) FROM billing.refunds"));

        await ScalarAsync(app, "UPDATE billing.jobs SET not_before = now() WHERE status = 'queued'");
        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));
        Assert.Equal(3L, await ScalarAsync(app, "SELECT count(*) FROM billing.refunds"));
    }

    [Fact(Timeout = 60_000)]
    public async Task AnEventDeliveryARebuildSettledWhileItsHandlerRanCommitsNoneOfTheHandlersWrites()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await CanceledOrderAsync(app);
        var held = app.GetRequiredService<Signals>().Hold(orderId);
        var delivering = Runner(app).RunNextAsync("Billing", "refunds", TestContext.Current.CancellationToken);
        await held.Started.Task;
        await EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid()));
        Assert.True(await Runner(app).RunNextAsync("Billing", "refunds", TestContext.Current.CancellationToken));

        held.Release.SetResult();
        await delivering;

        Assert.Equal<(Guid, string)>([(orderId, "succeeded")], await DeliveriesAsync(app));
        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM billing.refunds"));
        Assert.Equal(0L, await ScalarAsync(app, "SELECT count(*) FROM billing.jobs WHERE status = 'failed' OR last_error IS NOT NULL"));
    }

    [Fact]
    public async Task ARebuildRunTwiceGivesTheSameReadModel()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await CanceledOrderAsync(app);
        await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken);

        await EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid()));
        await EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid()));

        Assert.Equal(2, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));
        Assert.Equal(1L, await ScalarAsync(app, $"SELECT count(*) FROM billing.refunds WHERE order_id = '{orderId}'"));
        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM billing.refunds"));
    }

    [Fact]
    public async Task AFailedRebuildLeavesTheReadModelAndTheDeliveriesAsTheyWere()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await CanceledOrderAsync(app);
        await ScalarAsync(app, "UPDATE billing.jobs SET not_before = now() + interval '1 hour' WHERE status = 'queued'");
        var rebuild = await EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid(), FailAfterRebuild: true), new JobOptions(MaxAttempts: 1));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));

        Assert.Equal(JobStatus.Failed, (await JobAsync(app, "Billing", rebuild.Id))!.Status);
        Assert.Equal<(Guid, string)>([(orderId, "queued")], await DeliveriesAsync(app));
        Assert.Equal(0L, await ScalarAsync(app, "SELECT count(*) FROM billing.refunds"));
    }

    [Fact]
    public void ARebuiltEventHasNoOtherSubscriptionInItsModule()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddClaudaliskModuleJobs("Audit", "audit", jobs => jobs
            .Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled")
            .Rebuild<RebuildRefundsCommand, OrderCanceled, RebuildRefundsHandler>("rebuild-audit")
            .Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled")
            .Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled-again")));

        Assert.Contains("has no other subscription there", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARebuildRunsOnlyOnItsSubscriptionsLane()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => EnqueueAsync(app, new RebuildRefundsCommand(Guid.NewGuid()), new JobOptions(Lane: "other")));

        Assert.Contains("runs only on lane 'refunds'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ASubscriptionNeedsALaneThatExists()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddClaudaliskModuleJobs(
            "Audit", "audit", jobs => jobs.Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled", "audit")));

        Assert.Contains("no job lane 'audit'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARebuildNeedsASubscriptionToItsEvent()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddClaudaliskModuleJobs(
            "Audit", "audit", jobs => jobs.Rebuild<RebuildRefundsCommand, OrderCanceled, RebuildRefundsHandler>("rebuild-audit")));

        Assert.Contains("needs exactly one subscription", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARebuildLaneKeepsOneWorkerAtMost()
    {
        var before = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddClaudaliskModuleJobs("Audit", "audit", jobs => jobs
            .Lane("audit", 2)
            .Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled", "audit")
            .Rebuild<RebuildRefundsCommand, OrderCanceled, RebuildRefundsHandler>("rebuild-audit")));
        var after = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddClaudaliskModuleJobs("Audit", "audit", jobs => jobs
            .Lane("audit", 1)
            .Subscribe<OrderCanceled, RefundOnOrderCanceledHandler>("audit-order-canceled", "audit")
            .Rebuild<RebuildRefundsCommand, OrderCanceled, RebuildRefundsHandler>("rebuild-audit")
            .Lane("audit", 2)));

        Assert.Contains("one worker at most", before.Message, StringComparison.Ordinal);
        Assert.Contains("one worker at most", after.Message, StringComparison.Ordinal);
    }

    private static async Task<Guid> CanceledOrderAsync(IServiceProvider app)
    {
        var orderId = Guid.NewGuid();
        await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, $"order-{orderId:N}", FailAfterQueueing: false));
        await SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: false));
        return orderId;
    }

    private static async Task PublishAsync(IServiceProvider app, object integrationEvent)
    {
        await using var scope = app.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .RunAsync(token => scope.ServiceProvider.GetRequiredService<IEventPublisher>().PublishAsync(integrationEvent, token), TestContext.Current.CancellationToken);
    }

    private static async Task<List<(Guid OrderId, string Status)>> DeliveriesAsync(IServiceProvider app)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT (payload->>'orderId')::uuid, status FROM billing.jobs WHERE kind = 'refund-on-order-canceled' AND lane = 'refunds' ORDER BY created_at, id", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var deliveries = new List<(Guid, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            deliveries.Add((reader.GetGuid(0), reader.GetString(1)));
        }

        return deliveries;
    }

    private static async Task<object?> ScalarAsync(IServiceProvider app, string sql)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
