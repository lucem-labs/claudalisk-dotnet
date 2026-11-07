using Claudalisk.Dispatching;
using Claudalisk.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Claudalisk.Jobs.Tests;

public sealed class JobsTests
{
    [Fact]
    public async Task A_job_commits_with_the_data_that_queued_it()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();

        var job = await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, "A-1", FailAfterQueueing: false));

        Assert.Equal(("Orders", $"/api/jobs/orders/{job.Id}"), (job.Module, job.Url));
        Assert.Equal(1, await CountAsync(app, "orders.orders"));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task A_worker_about_to_wait_sees_a_job_queued_in_its_lane_so_it_does_not_miss_its_notify()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var transport = (PostgreSql.PostgreSqlJobTransport)app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var before = await transport.HasQueuedWorkAsync(orders, JobOptions.DefaultLane, default);

        await SendAsync<JobReference>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: false));

        Assert.False(before);
        Assert.True(await transport.HasQueuedWorkAsync(orders, JobOptions.DefaultLane, default));
        Assert.False(await transport.HasQueuedWorkAsync(orders, "search", default));
    }

    [Fact]
    public async Task A_job_rolls_back_with_the_data_that_queued_it()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<JobReference>(app, new PlaceOrderCommand(Guid.NewGuid(), "A-1", FailAfterQueueing: true)));

        Assert.Equal(0, await CountAsync(app, "orders.orders"));
        Assert.Equal(0, await CountAsync(app, "orders.jobs"));
    }

    [Fact]
    public async Task A_job_runs_its_command_and_succeeds_in_the_same_unit_of_work_with_its_result_link()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = Guid.NewGuid();
        var job = await SendAsync<JobReference>(app, new PlaceOrderCommand(orderId, "A-1", FailAfterQueueing: false));

        Assert.True(await Runner(app).RunNextAsync("Orders"));

        var record = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 1, $"/api/orders/{orderId}"), (record.Status, record.Attempts, record.ResultLink));
        Assert.Equal(1, await CountAsync(app, "orders.orders WHERE shipped"));
        Assert.False(await Runner(app).RunNextAsync("Orders"));
    }

    [Fact]
    public async Task Two_workers_never_claim_the_same_job()
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
            while (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), default) is { } claimed)
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
    public async Task A_failing_job_is_retried_and_then_succeeds()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 2));

        Assert.True(await Runner(app).RunNextAsync("Orders"));
        var afterFirst = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, 1, "/problems/jobs/failed"), (afterFirst.Status, afterFirst.Attempts, afterFirst.LastError!.Type));

        Assert.Equal(2, await Runner(app).RunUntilIdleAsync("Orders"));

        var done = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 3), (done.Status, done.Attempts));
    }

    [Fact]
    public async Task A_job_that_keeps_failing_stops_after_its_maximum_attempts()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 99), new JobOptions(MaxAttempts: 3));

        Assert.Equal(3, await Runner(app).RunUntilIdleAsync("Orders"));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 3), (failed.Status, failed.Attempts));
        Assert.Equal("The job failed.", failed.LastError!.Title);
        Assert.NotNull(failed.FinishedAt);
    }

    [Fact]
    public async Task A_retry_waits_for_its_backoff()
    {
        var connection = await PostgresServer.CreateDatabaseAsync();
        await using var app = new ServiceCollection().AddTestApp(connection)
            .Configure<JobsOptions>(options => options.RetryBaseDelay = TimeSpan.FromHours(1))
            .BuildServiceProvider();
        var job = await EnqueueAsync(app, new FlakyCommand(FailedAttempts: 1));

        Assert.True(await Runner(app).RunNextAsync("Orders"));

        var waiting = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal(JobStatus.Queued, waiting.Status);
        Assert.True(waiting.NotBefore > waiting.StartedAt);
        Assert.False(await Runner(app).RunNextAsync("Orders"));
    }

    [Fact]
    public async Task A_validation_failure_fails_the_job_at_once()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new StrictCommand(""));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders"));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/jobs/invalid"), (failed.Status, failed.Attempts, failed.LastError!.Type));
    }

    [Fact]
    public async Task A_domain_error_fails_the_job_at_once_with_its_problem_type()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new RefusedCommand("A-1"));

        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders"));

        var failed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Failed, 1, "/problems/orders/order-refused", "The order is refused."),
            (failed.Status, failed.Attempts, failed.LastError!.Type, failed.LastError.Title));
    }

    [Fact]
    public async Task A_job_whose_heartbeat_went_stale_goes_back_in_the_queue()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0));
        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        Assert.Equal(job.Id, (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), default))!.Id);
        Assert.Null(await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), default));

        await ExecuteAsync(app, $"UPDATE orders.jobs SET heartbeat_at = now() - interval '3 minutes' WHERE id = '{job.Id}'");

        var reclaimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), default);
        Assert.Equal((job.Id, 2), (reclaimed!.Id, reclaimed.Attempts));
    }

    [Fact]
    public async Task Canceling_a_queued_job_means_it_never_runs()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0));

        var canceled = await app.GetRequiredService<IJobTransport>().RequestCancelAsync(Registry(app).Find("Orders")!, job.Id, default);

        Assert.Equal(JobStatus.Canceled, canceled!.Status);
        Assert.False(await Runner(app).RunNextAsync("Orders"));
    }

    [Fact]
    public async Task Canceling_a_running_job_reaches_its_handler_through_the_token()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var key = Guid.NewGuid();
        var job = await EnqueueAsync(app, new WaitCommand(key, 0.5));
        var running = Runner(app).RunNextAsync("Orders");
        await app.GetRequiredService<Signals>().For(key).Started.Task;

        var progress = (await JobAsync(app, "Orders", job.Id))!;
        await app.GetRequiredService<IJobTransport>().RequestCancelAsync(Registry(app).Find("Orders")!, job.Id, default);
        await running;

        Assert.Equal((JobStatus.Running, 0.5, "waiting"), (progress.Status, progress.ProgressFraction, progress.ProgressStep));
        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task On_shutdown_a_running_job_goes_back_in_the_queue()
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
    public async Task A_dedupe_key_replaces_the_older_queued_job()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var first = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(DedupeKey: "rebuild"));
        var second = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(DedupeKey: "rebuild"));

        var replaced = (await JobAsync(app, "Orders", first.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (replaced.Status, replaced.LastError!.Type));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", second.Id))!.Status);
        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Orders"));
    }

    [Fact]
    public async Task Jobs_run_only_on_their_lane()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(Lane: "slow"));

        Assert.False(await Runner(app).RunNextAsync("Orders"));
        Assert.True(await Runner(app).RunNextAsync("Orders", "slow"));
        Assert.Equal(JobStatus.Succeeded, (await JobAsync(app, "Orders", job.Id))!.Status);
    }

    [Fact]
    public async Task A_batch_job_resumes_from_its_checkpoint_after_a_crash()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var job = await EnqueueAsync(app, new ImportOrdersCommand(Total: 7, CrashWhenDone: 4));

        Assert.True(await Runner(app).RunNextAsync("Orders"));
        var crashed = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Queued, """{"done":4}"""), (crashed.Status, crashed.Checkpoint!.Replace(" ", "")));
        Assert.Equal(4, await CountAsync(app, "orders.orders"));

        Assert.True(await Runner(app).RunNextAsync("Orders"));

        var done = (await JobAsync(app, "Orders", job.Id))!;
        Assert.Equal((JobStatus.Succeeded, 2, "/api/orders", 1.0), (done.Status, done.Attempts, done.ResultLink, done.ProgressFraction));
        Assert.Equal(7, await CountAsync(app, "orders.orders"));
        Assert.Equal(7, await CountAsync(app, "(SELECT DISTINCT reference FROM orders.orders) AS distinct_references"));
    }

    [Fact]
    public async Task A_rolled_back_change_delivers_no_event()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: true)));

        Assert.Equal(0, await CountAsync(app, "billing.jobs"));
        Assert.Equal(0, await CountAsync(app, "mail.jobs"));
    }

    [Fact]
    public async Task An_event_published_in_a_unit_of_work_that_later_fails_is_not_delivered()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await using (var scope = app.CreateAsyncScope())
        {
            await Assert.ThrowsAsync<TimeoutException>(() => scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
            {
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync<Guid>(new CancelOrderCommand(orderId, FailAfterCancel: false), token);
                throw new TimeoutException("The surrounding work failed after the event was published.");
            }));
        }

        Assert.Equal(0, await CountAsync(app, "billing.jobs"));
        Assert.Equal(0, await CountAsync(app, "mail.jobs"));
        Assert.Equal(0, await CountAsync(app, "orders.orders WHERE canceled"));
    }

    [Fact]
    public async Task An_event_reaches_every_subscriber_and_one_failing_subscriber_does_not_affect_the_others()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var orderId = await PlacedOrderAsync(app);

        await SendAsync<Guid>(app, new CancelOrderCommand(orderId, FailAfterCancel: false));

        Assert.Equal(1, await CountAsync(app, "billing.jobs"));
        Assert.Equal(1, await CountAsync(app, "mail.jobs"));
        Assert.Equal(5, await Runner(app).RunUntilIdleAsync("Mail"));
        Assert.Equal(1, await Runner(app).RunUntilIdleAsync("Billing"));
        Assert.Equal(1, await CountAsync(app, $"billing.refunds WHERE order_id = '{orderId}'"));
        Assert.Equal(1, await CountAsync(app, "billing.jobs WHERE status = 'succeeded'"));
        Assert.Equal(1, await CountAsync(app, "mail.jobs WHERE status = 'failed' AND attempts = 5"));
    }

    [Fact]
    public async Task Queueing_a_command_no_module_registered_names_the_command()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => EnqueueAsync(app, new PlaceOrderCommand(Guid.NewGuid(), "x", false)));

        Assert.Contains(nameof(PlaceOrderCommand), error.Message);
    }

    [Fact]
    public async Task Without_Docker_the_database_tests_fail_and_say_Docker_is_needed()
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
