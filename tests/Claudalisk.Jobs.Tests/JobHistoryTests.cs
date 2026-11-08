using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using static Claudalisk.Jobs.Tests.JobsTests;

namespace Claudalisk.Jobs.Tests;

public sealed class JobHistoryTests
{
    [Fact]
    public async Task AModuleListsItsOwnRunsNewestFirstWithTheirInputAndAFilter()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var first = await EnqueueAsync(app, new FlakyCommand(0));
        var second = await EnqueueAsync(app, new FlakyCommand(0));
        var refused = await EnqueueAsync(app, new RefusedCommand("only-refused"));
        await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        var queued = await EnqueueAsync(app, new FlakyCommand(0));

        var all = await ListAsync(app, new JobHistoryQuery());
        var flaky = await ListAsync(app, new JobHistoryQuery(Kind: "flaky", PageSize: 2, Page: 2));
        var failed = await ListAsync(app, new JobHistoryQuery(Statuses: [JobStatus.Failed]));

        Assert.Equal(4, all.Total);
        Assert.Equal([queued.Id, refused.Id, second.Id, first.Id], all.Runs.Select(run => run.Job.Id));
        Assert.Equal((3, first.Id), (flaky.Total, Assert.Single(flaky.Runs).Job.Id));
        var run = Assert.Single(failed.Runs);
        Assert.Equal((refused.Id, JobStatus.Failed), (run.Job.Id, run.Job.Status));
        Assert.Contains("only-refused", run.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AModuleDeletesAFinishedRunButNeverAQueuedOrRunningOne()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var finished = await EnqueueAsync(app, new FlakyCommand(0));
        await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        var running = await EnqueueAsync(app, new FlakyCommand(0));
        await app.GetRequiredService<IJobTransport>().ClaimAsync(Registry(app).Find("Orders")!, JobOptions.DefaultLane, TestContext.Current.CancellationToken);
        var queued = await EnqueueAsync(app, new FlakyCommand(0));

        Assert.Equal(
            (true, false, false, false),
            (await DeleteAsync(app, finished.Id), await DeleteAsync(app, running.Id), await DeleteAsync(app, queued.Id), await DeleteAsync(app, Guid.NewGuid())));

        Assert.Null(await JobAsync(app, "Orders", finished.Id));
        Assert.Equal(JobStatus.Running, (await JobAsync(app, "Orders", running.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", queued.Id))!.Status);
    }

    [Fact]
    public async Task DeletingARunJoinsTheModulesUnitOfWorkAndRollsBackWithIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var finished = await EnqueueAsync(app, new FlakyCommand(0));
        await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);

        await using (var scope = app.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<TimeoutException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
            {
                Assert.True(await scope.ServiceProvider.GetRequiredService<IJobHistory>().DeleteFinishedAsync("Orders", finished.Id, token));
                throw new TimeoutException("The module's command failed after it deleted the run.");
            }, TestContext.Current.CancellationToken));
        }

        Assert.NotNull(await JobAsync(app, "Orders", finished.Id));
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 501)]
    public async Task AHistoryPageOutsideItsBoundsIsRefused(int page, int pageSize)
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => ListAsync(app, new JobHistoryQuery(Page: page, PageSize: pageSize)));
    }

    [Fact]
    public async Task PruningDeletesOnlyFinishedRunsOlderThanTheModulesRetention()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var old = await EnqueueAsync(app, new FlakyCommand(0));
        var recent = await EnqueueAsync(app, new FlakyCommand(0));
        await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        var queued = await EnqueueAsync(app, new FlakyCommand(0));
        await FinishedAgoAsync(app, "orders", old.Id, days: 8);
        await FinishedAgoAsync(app, "orders", recent.Id, days: 6);
        await FinishedAgoAsync(app, "orders", queued.Id, days: 8);

        Assert.Equal(1, await Runner(app).PruneAsync("Orders", TestContext.Current.CancellationToken));

        Assert.Null(await JobAsync(app, "Orders", old.Id));
        Assert.NotNull(await JobAsync(app, "Orders", recent.Id));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", queued.Id))!.Status);
    }

    [Fact]
    public async Task AModuleWithNoRetentionOfItsOwnKeepsFinishedRunsForTheDefaultPeriod()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();
        await SendAsync<Claudalisk.Dispatching.JobReference>(app, new PlaceOrderCommand(orderId, $"order-{orderId:N}", FailAfterQueueing: false));
        await SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: false));
        await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken);
        var refund = Assert.Single((await app.GetRequiredService<IJobTransport>().ListAsync(Registry(app).Find("Billing")!, new JobHistoryQuery(), TestContext.Current.CancellationToken)).Runs);

        await FinishedAgoAsync(app, "billing", refund.Job.Id, days: 29);
        var keptAt29Days = await Runner(app).PruneAsync("Billing", TestContext.Current.CancellationToken);
        await FinishedAgoAsync(app, "billing", refund.Job.Id, days: 31);
        var deletedAt31Days = await Runner(app).PruneAsync("Billing", TestContext.Current.CancellationToken);

        Assert.Equal((JobStatus.Succeeded, 0, 1), (refund.Job.Status, keptAt29Days, deletedAt31Days));
    }

    [Fact]
    public void ARetentionMustBePositive()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentOutOfRangeException>(() => services.AddClaudaliskModuleJobs("Orders", "orders", jobs => jobs.Retention(TimeSpan.Zero)));
    }

    [Fact]
    public void AJobsOptionIntervalThatIsNotPositiveFailsWhenTheRunnerStarts()
    {
        using var app = new ServiceCollection()
            .AddTestApp("Host=localhost;Database=unused", options => options.CancelCheckInterval = TimeSpan.Zero)
            .BuildServiceProvider();

        Assert.Throws<Microsoft.Extensions.Options.OptionsValidationException>(() => app.GetRequiredService<JobRunner>());
    }

    private static async Task<JobHistoryPage> ListAsync(IServiceProvider app, JobHistoryQuery query)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobHistory>().ListAsync("Orders", query, TestContext.Current.CancellationToken);
    }

    private static async Task<bool> DeleteAsync(IServiceProvider app, Guid id)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .RunAsync(token => scope.ServiceProvider.GetRequiredService<IJobHistory>().DeleteFinishedAsync("Orders", id, token), TestContext.Current.CancellationToken);
    }

    private static async Task FinishedAgoAsync(IServiceProvider app, string schema, Guid id, int days)
    {
        await using var connection = await app.GetRequiredService<Npgsql.NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new Npgsql.NpgsqlCommand($"UPDATE {schema}.jobs SET finished_at = now() - make_interval(days => @days) WHERE id = @id", connection);
        command.Parameters.AddWithValue("days", days);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
