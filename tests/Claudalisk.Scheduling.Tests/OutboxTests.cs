using Claudalisk.Jobs;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Claudalisk.Scheduling.Tests;

public sealed class OutboxTests
{
    private static readonly TestSchedule Hourly = new("hourly-refresh", "0 * * * *");

    [Fact]
    public async Task AFireCommitsTheClaimAndTheQueuedJobTogether()
    {
        var (app, time, _) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));

        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero), await LastFiredAsync(app, "hourly-refresh"));
        var job = Assert.Single(await JobsAsync(app));
        Assert.Equal(("refresh-catalog", "queued", "hourly-refresh:2026-10-26T11:00:00.0000000Z"), (job.Kind, job.Status, job.DedupeKey));
        Assert.Contains("nightly", job.Payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFailureQueueingTheJobRollsTheClaimBackSoTheOccurrenceFiresAgain()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));
        await ExecuteAsync(app, "ALTER TABLE scheduling.jobs ADD CONSTRAINT ck_no_refresh CHECK (kind <> 'refresh-catalog')");

        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(TestApp.Start, await LastFiredAsync(app, "hourly-refresh"));
        Assert.Empty(await JobsAsync(app));
        await ExecuteAsync(app, "ALTER TABLE scheduling.jobs DROP CONSTRAINT ck_no_refresh");
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        await SchedulerTests.RunJobsAsync(app);
        Assert.Single(refreshes.Sources);
    }

    [Fact]
    public async Task AFailingScheduleDoesNotStopTheOthersFromFiring()
    {
        var (app, time, refreshes) = await StartAsync(new TestSchedule("broken", "0 * * * *", Command: "rebuild-index", Arguments: "{}"), Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));
        await ExecuteAsync(app, "ALTER TABLE scheduling.jobs ADD CONSTRAINT ck_no_rebuild CHECK (kind <> 'rebuild-index')");

        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        await SchedulerTests.RunJobsAsync(app);
        Assert.Single(refreshes.Sources);
        Assert.Equal(TestApp.Start, await LastFiredAsync(app, "broken"));
    }

    [Fact]
    public async Task RunningTheSchedulingJobsSendsTheCommandThroughTheDispatcher()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        Assert.Empty(refreshes.Sources);

        Assert.Equal(1, await SchedulerTests.RunJobsAsync(app));

        Assert.Equal(["nightly"], refreshes.Sources);
        Assert.Equal("succeeded", Assert.Single(await JobsAsync(app)).Status);
    }

    [Fact]
    public async Task TheSameOccurrenceClaimedTwiceYieldsOneQueuedJob()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        await ExecuteAsync(app, "UPDATE scheduling.schedules SET last_fired_timestamp = '2026-10-26T10:30:00Z'");

        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["canceled", "queued"], (await JobsAsync(app)).Select(job => job.Status).Order(StringComparer.Ordinal));
        await SchedulerTests.RunJobsAsync(app);
        Assert.Single(refreshes.Sources);
    }

    [Fact]
    public async Task AnAppsJobQueueNeverLandsInTheSchedulersJobsTable()
    {
        var (app, _, _) = await StartAsync(Hourly);
        await using var _ = app;
        await using var scope = app.CreateAsyncScope();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => scope.ServiceProvider.GetRequiredService<IJobQueue>().EnqueueAsync(new RefreshCatalogCommand("x"), cancellationToken: TestContext.Current.CancellationToken));

        Assert.Contains("No module registers", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARemovedAndReAddedScheduleStartsFreshWithoutCatchUp()
    {
        var connection = await TestApp.CreateDatabaseAsync();
        var time = new FakeTimeProvider(TestApp.Start);
        var other = new TestSchedule("other", "0 * * * *");
        await using (var first = TestApp.Build(connection, time, new Refreshes(), enabled: true, Hourly, other))
        {
            await first.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, await CountSchedulesAsync(first));
        }

        time.Advance(TimeSpan.FromHours(3));
        await using (var second = TestApp.Build(connection, time, new Refreshes(), enabled: true, other))
        {
            await second.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1, await CountSchedulesAsync(second));
        }

        var refreshes = new Refreshes();
        await using var third = TestApp.Build(connection, time, refreshes, enabled: true, Hourly, other);
        var scheduler = third.GetRequiredService<Scheduler>();
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(time.GetUtcNow(), await LastFiredAsync(third, "hourly-refresh"));
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(["hourly-refresh", "other"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<(ServiceProvider App, FakeTimeProvider Time, Refreshes Refreshes)> StartAsync(params TestSchedule[] schedules)
    {
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        return (TestApp.Build(await TestApp.CreateDatabaseAsync(), time, refreshes, enabled: true, schedules), time, refreshes);
    }

    private static async Task ExecuteAsync(IServiceProvider app, string sql)
    {
        await using var command = app.GetRequiredService<NpgsqlDataSource>().CreateCommand(sql);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<DateTimeOffset?> LastFiredAsync(IServiceProvider app, string name)
    {
        await using var command = app.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT last_fired_timestamp FROM scheduling.schedules WHERE name = @name");
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is DateTime value ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    private static async Task<long> CountSchedulesAsync(IServiceProvider app)
    {
        await using var command = app.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT count(*) FROM scheduling.schedules");
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }

    private static async Task<IReadOnlyList<(string Kind, string Status, string? DedupeKey, string Payload)>> JobsAsync(IServiceProvider app)
    {
        await using var command = app.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT kind, status, dedupe_key, payload::text FROM scheduling.jobs ORDER BY created_at, id");
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var jobs = new List<(string, string, string?, string)>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            jobs.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3)));
        }

        return jobs;
    }
}
