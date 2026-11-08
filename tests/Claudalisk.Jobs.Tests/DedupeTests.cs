using Claudalisk.Dispatching;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static Claudalisk.Jobs.Tests.JobsTests;

namespace Claudalisk.Jobs.Tests;

public sealed class DedupeTests
{
    private static readonly JobError Failure = new("/problems/jobs/failed", "The job failed.", null);

    [Fact]
    public async Task TheSameKeyOnDifferentKindsLeavesBothQueued()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        var flaky = await EnqueueAsync(app, new FlakyCommand(0), new JobOptions(DedupeKey: "same"));
        var strict = await EnqueueAsync(app, new StrictCommand("A-1"), new JobOptions(DedupeKey: "same"));

        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", flaky.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", strict.Id))!.Status);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoConcurrentEnqueuesWithTheSameKindAndKeyLeaveOneQueuedJob()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var firstQueued = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var commitFirst = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new JobOptions(DedupeKey: "rebuild");
        var first = Task.Run(async () =>
        {
            await using var scope = app.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
            {
                var queued = await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new FlakyCommand(0), options, token);
                firstQueued.SetResult();
                await commitFirst.Task.WaitAsync(token);
                return queued;
            });
        });
        await firstQueued.Task;
        var second = EnqueueAsync(app, new FlakyCommand(0), options);

        var polls = 0;
        while (Equals(await ScalarAsync(app, WaitingOnAdvisoryLock), 0L))
        {
            Assert.True(++polls <= 2000, "The second enqueue never blocked on the advisory lock.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        commitFirst.SetResult();
        var firstJob = await first;
        var secondJob = await second;

        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM orders.jobs WHERE dedupe_key = 'rebuild' AND status = 'queued'"));
        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", firstJob.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", secondJob.Id))!.Status);
    }

    [Fact]
    public async Task TheDedupeIndexCancelsOlderQueuedDuplicatesAndThenRefusesNewOnes()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await ScalarAsync(app, Insert("older", "now() - interval '1 minute'") + Insert("newer", "now()") + Insert("other-kind", "now()", kind: "second"));

        await ScalarAsync(app, PostgreSql.JobsTable.CreateDedupeIndexSql("billing"));

        Assert.Equal((1L, 0L), await DedupeIndexesAsync(app));
        Assert.Equal<(string, string)>(
            [("newer", "queued"), ("older", "canceled"), ("other-kind", "queued")],
            await StatusesAsync(app));
        Assert.Equal("/problems/jobs/superseded", await ScalarAsync(app, "SELECT last_error->>'type' FROM billing.jobs WHERE payload->>'name' = 'older'"));
        var refused = await Assert.ThrowsAsync<PostgresException>(() => ScalarAsync(app, Insert("again", "now()")));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, refused.SqlState);

        await ScalarAsync(app, PostgreSql.JobsTable.DropDedupeIndexSql("billing"));
        Assert.Equal((0L, 1L), await DedupeIndexesAsync(app));
        await ScalarAsync(app, Insert("again", "now()"));

        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM billing.jobs WHERE payload->>'name' = 'again'"));
    }

    [Fact]
    public async Task ARetryOfAJobWhoseKeyANewerQueuedJobHoldsSupersedesItInsteadOfRequeueingIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var (transport, orders, running, newer) = await RunningWithANewerTwinAsync(app);

        await transport.RetryOrFailAsync(orders, running.Id, Failure, TimeSpan.Zero, TestContext.Current.CancellationToken);

        var old = (await JobAsync(app, "Orders", running.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (old.Status, old.LastError!.Type));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", newer.Id))!.Status);
    }

    [Fact]
    public async Task AShutdownRequeueOfAJobWhoseKeyANewerQueuedJobHoldsSupersedesIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var (transport, orders, running, newer) = await RunningWithANewerTwinAsync(app);

        await transport.RequeueAsync(orders, running.Id, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", running.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", newer.Id))!.Status);
    }

    [Fact]
    public async Task TheStaleSweepRequeuesOnlyTheNewestOfSeveralRunningJobsWithOneKey()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var (transport, orders, older, newer) = await RunningWithANewerTwinAsync(app);
        var reclaimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        await ScalarAsync(app, "UPDATE orders.jobs SET heartbeat_at = now() - interval '3 minutes' WHERE status = 'running'");

        var claimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.Equal((newer.Id, newer.Id), (reclaimed!.Id, claimed!.Id));
        var old = (await JobAsync(app, "Orders", older.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (old.Status, old.LastError!.Type));
    }

    private const string WaitingOnAdvisoryLock =
        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    private static async Task<(IJobTransport Transport, JobsModule Orders, ClaimedJob Running, JobReference Newer)> RunningWithANewerTwinAsync(IServiceProvider app)
    {
        var options = new JobOptions(DedupeKey: "rebuild");
        await EnqueueAsync(app, new FlakyCommand(0), options);
        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var running = (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken))!;
        var newer = await EnqueueAsync(app, new FlakyCommand(0), options);
        return (transport, orders, running, newer);
    }

    [Fact]
    public async Task TheRunningIndexExistsAfterTheMigrations()
    {
        await using var app = await PostgresServer.CreateAppAsync();

        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'orders' AND indexname = 'ix_jobs_running'"));
    }

    private static async Task<(long Unique, long Plain)> DedupeIndexesAsync(IServiceProvider app) =>
        ((long)(await ScalarAsync(app, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'billing' AND indexname = 'ux_jobs_dedupe'"))!,
        (long)(await ScalarAsync(app, "SELECT count(*) FROM pg_indexes WHERE schemaname = 'billing' AND indexname = 'ix_jobs_dedupe'"))!);

    private static string Insert(string name, string createdAt, string kind = "first") =>
        $"INSERT INTO billing.jobs (id, kind, payload, lane, status, max_attempts, dedupe_key, created_at) VALUES (gen_random_uuid(), '{kind}', '{{\"name\":\"{name}\"}}', 'default', 'queued', 5, 'k', {createdAt});";

    private static async Task<List<(string Name, string Status)>> StatusesAsync(IServiceProvider app)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT payload->>'name', status FROM billing.jobs ORDER BY payload->>'name'", connection);
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var rows = new List<(string, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            rows.Add((reader.GetString(0), reader.GetString(1)));
        }

        return rows;
    }

    private static async Task<object?> ScalarAsync(IServiceProvider app, string sql)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
    }
}
