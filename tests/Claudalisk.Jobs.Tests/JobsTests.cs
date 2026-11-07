using Claudalisk.Dispatching;
using Claudalisk.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Jobs.Tests;

public sealed class JobsTests
{
    [Fact]
    public async Task AJobCommitsWithTheDataThatQueuedIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();

        var job = await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, "A-1", FailAfterQueueing: false));

        Assert.Equal(("Orders", $"/api/jobs/orders/{job.Id}"), (job.Module, job.Url));
        Assert.Equal(1, await CountAsync(app, "orders.orders"));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task AWorkerAboutToWaitSeesAJobQueuedInItsLaneSoItDoesNotMissItsNotify()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var transport = (PostgreSql.PostgreSqlJobTransport)app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var before = await transport.HasQueuedWorkAsync(orders, JobOptions.DefaultLane, TestContext.Current.CancellationToken);

        await SendAsync<JobReference>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: false));

        Assert.False(before);
        Assert.True(await transport.HasQueuedWorkAsync(orders, JobOptions.DefaultLane, TestContext.Current.CancellationToken));
        Assert.False(await transport.HasQueuedWorkAsync(orders, "search", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AJobRollsBackWithTheDataThatQueuedIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<JobReference>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: true)));

        Assert.Equal(0, await CountAsync(app, "orders.orders"));
        Assert.Equal(0, await CountAsync(app, "orders.jobs"));
    }

    [Fact]
    public async Task AJobRunsItsCommandAndSucceedsInTheSameUnitOfWorkWithItsResultLink()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();
        var job = await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, "A-1", FailAfterQueueing: false));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var record = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 1, $"/api/orders/{orderId}"), (record.Status, record.Attempts, record.ResultLink));
        Assert.Equal(1, await CountAsync(app, "orders.orders WHERE shipped"));
        Assert.False(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TwoWorkersNeverClaimTheSameJob()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var queued = new List<Guid>();
        for (var i = 0; i < 20; i++)
        {
            queued.Add((await EnqueueAsync(app, new FlakyCommand(0))).Id);
        }

        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var claims = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            var mine = new List<Guid>();
            while (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken) is { } claimed)
            {
                mine.Add(claimed.Id);
            }

            return mine;
        }));

        var all = claims.SelectMany(ids => ids).ToArray();
        Assert.Equal(queued.Order(), all.Order());
        Assert.Equal(all.Length, all.Distinct().Count());
    }

    [Fact]
    public async Task AFailingJobIsRetriedAndThenSucceeds()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 2));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
        var afterFirst = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, 1, "/problems/jobs/failed"), (afterFirst.Status, afterFirst.Attempts, afterFirst.LastError!.Type));

        Assert.Equal(2, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var done = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 3), (done.Status, done.Attempts));
    }

    [Fact]
    public async Task AJobThatKeepsFailingStopsAfterItsMaximumAttempts()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 99), new JobOptions(MaxAttempts: 3));

        Assert.Equal(3, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 3), (failed.Status, failed.Attempts));
        Assert.Equal("The job failed.", failed.LastError!.Title);
        Assert.NotNull(failed.FinishedAt);
    }

    [Fact]
    public async Task ARetryWaitsForItsBackoff()
    {
        var connection = await PostgresServer.CreateDatabaseAsync();
        await using var app = new ServiceCollection().AddTestApp(connection)
            .Configure<JobsOptions>(options => options.RetryBaseDelay = TimeSpan.FromHours(1))
            .BuildServiceProvider();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 1));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var waiting = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal(JobStatus.Queued, waiting.Status);
        Assert.True(waiting.NotBefore > waiting.StartedAt);
        Assert.False(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AValidationFailureFailsTheJobAtOnce()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new StrictCommand(""));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/jobs/invalid"), (failed.Status, failed.Attempts, failed.LastError!.Type));
    }

    [Fact]
    public async Task ADomainErrorFailsTheJobAtOnceWithItsProblemType()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new RefusedCommand("A-1"));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/orders/order-refused", "The order is refused."),
            (failed.Status, failed.Attempts, failed.LastError!.Type, failed.LastError.Title));
    }

    [Fact]
    public async Task AnUnavailableDependencyIsRetriedNoSoonerThanTheWaitItCarries()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new OutageCommand(WaitMinutes: 60));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var waiting = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, 1, "/problems/dependency-unavailable"), (waiting.Status, waiting.Attempts, waiting.LastError!.Type));
        Assert.True(waiting.NotBefore >= waiting.StartedAt + TimeSpan.FromMinutes(60));
        Assert.False(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AnUnavailableDependencyWithNoWaitIsRetriedWithBackoffUntilItsAttemptsRunOut()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new OutageCommand(WaitMinutes: 0), new JobOptions(MaxAttempts: 3));

        Assert.Equal(3, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 3, "/problems/dependency-unavailable", "A service this job needs is unavailable."),
            (failed.Status, failed.Attempts, failed.LastError!.Type, failed.LastError.Title));
    }

    [Fact]
    public async Task AJobWhoseHeartbeatWentStaleGoesBackInTheQueue()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0));
        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        Assert.Equal(job.Id, (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken))!.Id);
        Assert.Null(await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken));

        await ExecuteAsync(app, $"UPDATE orders.jobs SET heartbeat_at = now() - interval '3 minutes' WHERE id = '{job.Id}'");

        var reclaimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        Assert.Equal((job.Id, 2), (reclaimed!.Id, reclaimed.Attempts));
    }

    [Fact]
    public async Task CancelingAQueuedJobMeansItNeverRuns()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0));

        var canceled = await app.GetRequiredService<IJobTransport>().RequestCancelAsync(Registry(app).Find("Orders")!, job.Id, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Canceled, canceled!.Status);
        Assert.False(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task CancelingARunningJobReachesItsHandlerThroughTheToken()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var key = Guid.NewGuid();
        var job = await EnqueueAsync(app, new WaitCommand(key, 0.5));
        var running = Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        await app.GetRequiredService<Signals>().For(key).Started.Task;

        var progress = (await JobAsync(app, "Orders", job.Id))!;
        await app.GetRequiredService<IJobTransport>().RequestCancelAsync(Registry(app).Find("Orders")!, job.Id, TestContext.Current.CancellationToken);
        await running;

        Assert.Equal((JobStatus.Running, 0.5, "waiting"), (progress.Status, progress.ProgressFraction, progress.ProgressStep));
        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task OnShutdownARunningJobGoesBackInTheQueue()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var key = Guid.NewGuid();
        var job = await EnqueueAsync(app, new WaitCommand(key, 0.1));
        using var stopping = new CancellationTokenSource();
        var running = Runner(app).RunNextAsync("Orders", cancellationToken: stopping.Token);
        await app.GetRequiredService<Signals>().For(key).Started.Task;

        await stopping.CancelAsync();
        await running;

        var requeued = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, 0), (requeued.Status, requeued.Attempts));
    }

    [Fact]
    public async Task ADedupeKeyReplacesTheOlderQueuedJob()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var first = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(DedupeKey: "rebuild"));
        var second = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(DedupeKey: "rebuild"));

        var replaced = (await JobAsync(app, "Orders", first.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (replaced.Status, replaced.LastError!.Type));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", second.Id))!.Status);
        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task JobsRunOnlyOnTheirLane()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(Lane: "slow"));

        Assert.False(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
        Assert.True(await Runner(app).RunNextAsync("Orders", "slow", TestContext.Current.CancellationToken));
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task ABatchJobResumesFromItsCheckpointAfterACrash()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new ImportOrdersCommand(Total: 7, CrashWhenDone: 4));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
        var crashed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, """{"done":4}"""), (crashed.Status, crashed.Checkpoint!.Replace(" ", "")));
        Assert.Equal(4, await CountAsync(app, "orders.orders"));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var done = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 2, "/api/orders", 1.0), (done.Status, done.Attempts, done.ResultLink, done.ProgressFraction));
        Assert.Equal(7, await CountAsync(app, "orders.orders"));
        Assert.Equal(7, await CountAsync(app, "(SELECT DISTINCT reference FROM orders.orders) AS distinct_references"));
    }

    [Fact]
    public async Task ABatchThatWritesNothingRunsOutsideATransactionAndTheNextBatchWritesItsOutput()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await PlacedOrderAsync(app);
        var id = Guid.NewGuid();
        var job = await EnqueueAsync(app, new ScoreOrdersCommand(id, CrashOnWrite: false, ChangeWhileComputing: false));

        Assert.Equal(2, await Runner(app).RunUntilIdleAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var done = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, $"/api/orders/{id}"), (done.Status, done.ResultLink));
        Assert.Equal("""{"orders":1,"computedInATransaction":false}""", done.Checkpoint!.Replace(" ", ""));
        Assert.Equal(1, await CountAsync(app, $"orders.orders WHERE id = '{id}' AND reference = 'score-1'"));
    }

    [Fact]
    public async Task ARetryAfterABatchThatWritesNothingResumesFromItsOutputWithoutComputingAgain()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var id = Guid.NewGuid();
        var job = await EnqueueAsync(app, new ScoreOrdersCommand(id, CrashOnWrite: true, ChangeWhileComputing: false));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));
        var crashed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, 0.9), (crashed.Status, crashed.ProgressFraction));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        Assert.Equal(JobStatus.Succeeded, (await JobAsync(app, "Orders", job.Id))!.Status);
        Assert.Equal(1, app.GetRequiredService<Computations>().Of(id));
        Assert.Equal(1, await CountAsync(app, $"orders.orders WHERE id = '{id}'"));
    }

    [Fact]
    public async Task ABatchThatWritesNothingButChangesAnAggregateFailsWithoutSavingItsOutput()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new ScoreOrdersCommand(Guid.NewGuid(), CrashOnWrite: false, ChangeWhileComputing: true), new JobOptions(MaxAttempts: 1));

        Assert.True(await Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, "/problems/jobs/failed", null), (failed.Status, failed.LastError!.Type, failed.Checkpoint));
        Assert.Equal(0, await CountAsync(app, "billing.jobs"));
    }

    [Fact(Timeout = 60_000)]
    public async Task ACancelRequestReachesARunningJobWithoutWaitingForTheHeartbeat()
    {
        await using var app = await PostgresServer.CreateAppAsync(options =>
        {
            options.HeartbeatInterval = TimeSpan.FromHours(1);
            options.CancelCheckInterval = TimeSpan.FromMilliseconds(20);
        });
        var key = Guid.NewGuid();
        var job = await EnqueueAsync(app, new WaitCommand(key, 0.5));
        var running = Runner(app).RunNextAsync("Orders", cancellationToken: TestContext.Current.CancellationToken);
        await app.GetRequiredService<Signals>().For(key).Started.Task;

        await app.GetRequiredService<IJobTransport>().RequestCancelAsync(Registry(app).Find("Orders")!, job.Id, TestContext.Current.CancellationToken);
        await running;

        var canceled = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Canceled, true), (canceled.Status, canceled.CancelRequested));
    }

    [Fact]
    public async Task ARolledBackChangeDeliversNoEvent()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: true)));

        Assert.Equal(0, await CountAsync(app, "billing.jobs"));
        Assert.Equal(0, await CountAsync(app, "mail.jobs"));
    }

    [Fact]
    public async Task AnEventPublishedInAUnitOfWorkThatLaterFailsIsNotDelivered()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await using (var scope = app.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<TimeoutException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
            {
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<Guid>(new CancelOrderCommand(orderId, FailAfterCancel: false), token);
                throw new TimeoutException("The surrounding work failed after the event was published.");
            }, TestContext.Current.CancellationToken));
        }

        Assert.Equal(0, await CountAsync(app, "billing.jobs"));
        Assert.Equal(0, await CountAsync(app, "mail.jobs"));
        Assert.Equal(0, await CountAsync(app, "orders.orders WHERE canceled"));
    }

    [Fact]
    public async Task AnEventReachesEverySubscriberAndOneFailingSubscriberDoesNotAffectTheOthers()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: false));

        Assert.Equal(1, await CountAsync(app, "billing.jobs"));
        Assert.Equal(1, await CountAsync(app, "mail.jobs"));
        Assert.Equal(5, await Runner(app).RunUntilIdleAsync("Mail", cancellationToken: TestContext.Current.CancellationToken));
        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing", "refunds", TestContext.Current.CancellationToken));
        Assert.Equal(1, await CountAsync(app, $"billing.refunds WHERE order_id = '{orderId}'"));
        Assert.Equal(1, await CountAsync(app, "billing.jobs WHERE status = 'succeeded'"));
        Assert.Equal(1, await CountAsync(app, "mail.jobs WHERE status = 'failed' AND attempts = 5"));
    }

    [Fact]
    public async Task QueueingACommandNoModuleRegisteredNamesTheCommand()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EnqueueAsync(app, new PlaceOrderCommand(Guid.NewGuid(), "x", false)));

        Assert.Contains(nameof(PlaceOrderCommand), error.Message);
    }

    [Fact]
    public async Task WithoutDockerTheDatabaseTestsFailAndSayDockerIsNeeded()
    {
        var unreachable = new InvalidOperationException("Docker is either not running or misconfigured.");

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => PostgresServer.StartOrExplainAsync(() => Task.FromException(unreachable)));

        Assert.StartsWith("The database tests need Docker", failure.Message);
    }

    internal static JobRunner Runner(IServiceProvider app) => app.GetRequiredService<JobRunner>();

    internal static JobsRegistry Registry(IServiceProvider app) => app.GetRequiredService<JobsRegistry>();

    internal static Task<JobRecord?> JobAsync(IServiceProvider app, string module, Guid id) =>
        app.GetRequiredService<IJobTransport>().GetAsync(Registry(app).Find(module)!, id, default);

    internal static async Task<JobReference> EnqueueAsync(IServiceProvider app, object command, JobOptions? options = null)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>()
            .RunAsync(token => scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(command, options, token));
    }

    internal static async Task<T> SendAsync<T>(IServiceProvider app, object message)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<T>(message);
    }

    private static async Task<Guid> PlacedOrderAsync(IServiceProvider app)
    {
        var orderId = Guid.NewGuid();
        await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, $"order-{orderId:N}", FailAfterQueueing: false));
        return orderId;
    }

    private static async Task<long> CountAsync(IServiceProvider app, string from)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand($"SELECT count(*) FROM {from}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(IServiceProvider app, string sql)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
