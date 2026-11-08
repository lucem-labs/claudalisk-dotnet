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

        await transport.RetryOrFailAsync(orders, running.Id, running.Attempts, Failure, TimeSpan.Zero, TestContext.Current.CancellationToken);

        var old = (await JobAsync(app, "Orders", running.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (old.Status, old.LastError!.Type));
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", newer.Id))!.Status);
    }

    [Fact]
    public async Task AShutdownRequeueOfAJobWhoseKeyANewerQueuedJobHoldsSupersedesIt()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var (transport, orders, running, newer) = await RunningWithANewerTwinAsync(app);

        await transport.RequeueAsync(orders, running.Id, running.Attempts, TestContext.Current.CancellationToken);

        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", running.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", newer.Id))!.Status);
    }

    [Fact]
    public async Task TheStaleSweepRequeuesOnlyTheNewestOfSeveralRunningJobsWithOneKey()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var (transport, orders, older, newer) = await RunningWithANewerTwinAsync(app);
        var reclaimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TestContext.Current.CancellationToken);
        await ScalarAsync(app, "UPDATE orders.jobs SET heartbeat_at = now() - interval '3 minutes' WHERE status = 'running'");

        await transport.SweepStaleAsync(orders, TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);
        var claimed = await transport.ClaimAsync(orders, JobOptions.DefaultLane, TestContext.Current.CancellationToken);

        Assert.Equal((newer.Id, newer.Id), (reclaimed!.Id, claimed!.Id));
        var old = (await JobAsync(app, "Orders", older.Id))!;
        Assert.Equal((JobStatus.Canceled, "/problems/jobs/superseded"), (old.Status, old.LastError!.Type));
    }

    [Fact]
    public async Task TheClaimUsesThePartialIndexWithoutSorting()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await ScalarAsync(app, """
            INSERT INTO orders.jobs (id, kind, payload, lane, status, max_attempts, not_before)
            SELECT gen_random_uuid(), 'k', '{}'::jsonb, CASE WHEN i % 2 = 0 THEN 'default' ELSE 'slow' END, CASE WHEN i % 10 = 0 THEN 'queued' ELSE 'succeeded' END, 1, now() - i * interval '1 second'
            FROM generate_series(1, 20000) AS i;
            ANALYZE orders.jobs;
            """);

        var plan = (string)(await ScalarAsync(app, """
            EXPLAIN (FORMAT JSON) SELECT id FROM orders.jobs WHERE lane = 'default' AND status = 'queued' AND not_before <= now()
            ORDER BY not_before, created_at FOR UPDATE SKIP LOCKED LIMIT 1
            """))!;

        Assert.Contains("ix_jobs_claim_queued", plan, StringComparison.Ordinal);
        Assert.DoesNotContain("Sort", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AStaleRunCannotWriteOntoARowAnotherWorkerReclaimed()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var token = TestContext.Current.CancellationToken;
        await EnqueueAsync(app, new FlakyCommand(0), new JobOptions());
        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var stale = (await transport.ClaimAsync(orders, JobOptions.DefaultLane, token))!;
        await ScalarAsync(app, "UPDATE orders.jobs SET heartbeat_at = now() - interval '3 minutes'");
        await transport.SweepStaleAsync(orders, TimeSpan.FromMinutes(2), token);
        var reclaimed = (await transport.ClaimAsync(orders, JobOptions.DefaultLane, token))!;
        await using var scope = app.CreateAsyncScope();
        var writer = scope.ServiceProvider.GetRequiredService<IJobWriter>();

        Assert.False(await transport.HeartbeatAsync(orders, stale.Id, stale.Attempts, token));
        await transport.RetryOrFailAsync(orders, stale.Id, stale.Attempts, new JobError("/problems/late", "Late.", null), null, token);
        await transport.MarkCanceledAsync(orders, stale.Id, stale.Attempts, token);
        await transport.RequeueAsync(orders, stale.Id, stale.Attempts, token);
        Assert.False(await writer.SaveCheckpointAsync(orders, stale.Id, stale.Attempts, "{}", null, token));

        var job = (await JobAsync(app, "Orders", reclaimed.Id))!;
        Assert.Equal(JobStatus.Running, job.Status);
        Assert.Equal(2, job.Attempts);
        Assert.Null(job.LastError);
    }

    [Fact(Timeout = 60_000)]
    public async Task ARequeueWaitsForTheDedupeLockAndThenSeesTheTwin()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        var token = TestContext.Current.CancellationToken;
        var (transport, orders, running, newer) = await RunningWithANewerTwinAsync(app);
        await using var holder = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(token);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", holder))
        {
            hold.Parameters.AddWithValue("key", PostgreSql.JobsTable.DedupeLockKey(orders, running.Kind, "rebuild"));
            await hold.ExecuteNonQueryAsync(token);
        }

        var requeue = transport.RequeueAsync(orders, running.Id, running.Attempts, token);
        var polls = 0;
        while (!Equals(await ScalarAsync(app, WaitingOnAdvisoryLock), 1L))
        {
            Assert.True(++polls <= 2000, "The requeue never blocked on the dedupe lock.");
            await Task.Delay(10, token);
        }

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", holder))
        {
            await release.ExecuteNonQueryAsync(token);
        }

        await requeue;

        Assert.Equal(JobStatus.Canceled, (await JobAsync(app, "Orders", running.Id))!.Status);
        Assert.Equal(JobStatus.Queued, (await JobAsync(app, "Orders", newer.Id))!.Status);
    }

    private const string WaitingOnAdvisoryLock =
        "SELECT count(*) FROM pg_locks WHERE locktype = 'advisory' AND NOT granted AND database = (SELECT oid FROM pg_database WHERE datname = current_database())";

    private static async Task<(IJobTransport Transport, JobsModule Orders, ClaimedJob Running, JobReference Newer)> RunningWithANewerTwinAsync(IServiceProvider app)
    {
        var options = new JobOptions(DedupeKey: "rebuild");
        await EnqueueAsync(app, new FlakyCommand(0), options);
        var transport = app.GetRequiredService<IJobTransport>();
        var orders = Registry(app).Find("Orders")!;
        var running = (await transport.ClaimAsync(orders, JobOptions.DefaultLane, TestContext.Current.CancellationToken))!;
        var newer = await EnqueueAsync(app, new FlakyCommand(0), options);
        return (transport, orders, running, newer);
    }

    [Fact(Timeout = 60_000)]
    public async Task TwoConcurrentEnqueuesOutsideAUnitOfWorkLeaveOneQueuedJobAndNoError()
    {
        await using var app = await PostgresServer.CreateAppAsync();
        await using var holder = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using (var hold = new NpgsqlCommand("SELECT pg_advisory_lock(hashtextextended(@key, 0))", holder))
        {
            hold.Parameters.AddWithValue("key", PostgreSql.JobsTable.DedupeLockKey(Registry(app).Find("Orders")!, "flaky", "rebuild"));
            await hold.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        var options = new JobOptions(DedupeKey: "rebuild");
        var both = new[] { EnqueueOutsideAsync(app, options), EnqueueOutsideAsync(app, options) };
        var polls = 0;
        while (!Equals(await ScalarAsync(app, WaitingOnAdvisoryLock), 2L))
        {
            Assert.True(++polls <= 2000, "The enqueues never both blocked on the advisory lock.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }

        await using (var release = new NpgsqlCommand("SELECT pg_advisory_unlock_all()", holder))
        {
            await release.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await Task.WhenAll(both);

        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM orders.jobs WHERE dedupe_key = 'rebuild' AND status = 'queued'"));
        Assert.Equal(1L, await ScalarAsync(app, "SELECT count(*) FROM orders.jobs WHERE dedupe_key = 'rebuild' AND status = 'canceled'"));
    }

    private static async Task<JobReference> EnqueueOutsideAsync(IServiceProvider app, JobOptions options)
    {
        await using var scope = app.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new FlakyCommand(0), options, TestContext.Current.CancellationToken);
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
