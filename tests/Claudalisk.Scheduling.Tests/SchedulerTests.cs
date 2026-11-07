using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Claudalisk.Scheduling.Tests;

public sealed class SchedulerTests
{
    private static readonly TestSchedule Hourly = new("hourly-refresh", "0 * * * *");

    [Fact]
    public async Task AScheduleFiresOncePerScheduledTime()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();

        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(29));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(59));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(["nightly", "nightly"], refreshes.Sources);
    }

    [Fact]
    public async Task MissedRunsAfterDowntimeBecomeOneCatchUpRun()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromHours(3).Add(TimeSpan.FromMinutes(45)));
        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 14, 0, 0, TimeSpan.Zero), await LastFiredAsync(app, "hourly-refresh"));
        time.Advance(TimeSpan.FromMinutes(44));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(["hourly-refresh"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Equal(2, refreshes.Sources.Count);
    }

    [Fact]
    public async Task TwoInstancesFireAScheduleOnce()
    {
        var connection = await TestApp.CreateDatabaseAsync();
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        await using var first = TestApp.Build(connection, time, refreshes, enabled: true, Hourly);
        await using var second = TestApp.Build(connection, time, refreshes, enabled: true, Hourly);
        await first.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));

        var fired = await Task.WhenAll(first.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken), second.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Single(fired.SelectMany(names => names));
        Assert.Single(refreshes.Sources);
    }

    [Fact]
    public async Task AnInstanceWaitsForAnotherInstancesLockAndThenDoesNotFireAgain()
    {
        var (app, time, refreshes) = await StartAsync(Hourly);
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));
        var dataSource = app.GetRequiredService<NpgsqlDataSource>();
        await using var other = await dataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var otherFiring = await other.BeginTransactionAsync(TestContext.Current.CancellationToken);
        await using (var lockRow = new NpgsqlCommand("SELECT last_fired_at FROM scheduling.schedules WHERE name = 'hourly-refresh' FOR UPDATE", other, otherFiring))
        {
            await lockRow.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        var waiting = scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        await WaitUntilBlockedOnALockAsync(dataSource);
        await using (var fire = new NpgsqlCommand("UPDATE scheduling.schedules SET last_fired_at = '2026-10-26T11:00:00Z' WHERE name = 'hourly-refresh'", other, otherFiring))
        {
            await fire.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await otherFiring.CommitAsync(TestContext.Current.CancellationToken);

        Assert.Empty(await waiting);
        Assert.Empty(refreshes.Sources);
    }

    [Fact]
    public async Task AScheduleFiresAtItsTimeInItsTimeZone()
    {
        var (app, time, refreshes) = await StartAsync(new TestSchedule("warsaw-morning", "0 9 * * *", TimeZone: "Europe/Warsaw"));
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 7, 59, 0, TimeSpan.Zero));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2026, 10, 27, 8, 0, 0, TimeSpan.Zero));
        Assert.Equal(["warsaw-morning"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Single(refreshes.Sources);
    }

    [Fact]
    public async Task AScheduleSendsItsConfiguredArgumentsThroughTheDispatcher()
    {
        var (app, time, refreshes) = await StartAsync(Hourly with { Arguments = """{"source":"weekly-sweep"}""" });
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromMinutes(30));

        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["weekly-sweep"], refreshes.Sources);
    }

    [Fact]
    public async Task SchedulesDoNotRunUnlessEnabled()
    {
        var connection = await TestApp.CreateDatabaseAsync();
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        await using var app = TestApp.Build(connection, time, refreshes, enabled: false, Hourly);
        var hosted = app.GetServices<IHostedService>().ToArray();

        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        await Assert.Single(hosted.OfType<BackgroundService>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(5));
        foreach (var service in hosted)
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(refreshes.Sources);
        Assert.Equal(0, await CountSchedulesAsync(app));
    }

    [Fact]
    public async Task AnEnabledSchedulerFiresOnItsTicks()
    {
        var connection = await TestApp.CreateDatabaseAsync();
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        await using var app = TestApp.Build(connection, time, refreshes, enabled: true, Hourly);
        await app.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken);
        var hosted = app.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        time.Advance(TimeSpan.FromMinutes(31));
        await refreshes.First.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        foreach (var service in hosted)
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Equal(["nightly"], refreshes.Sources);
    }

    [Fact]
    public async Task InvalidSchedulesFailWithEveryProblemNamed()
    {
        var (app, _, _) = await StartAsync(
            new TestSchedule("bad-cron", "every hour"),
            new TestSchedule("bad-zone", "0 * * * *", TimeZone: "Mars/Olympus"),
            new TestSchedule("unknown-command", "0 * * * *", Command: "refresh-everything"),
            new TestSchedule("wrong-module", "0 * * * *", Module: "Billing"),
            new TestSchedule("bad-arguments", "0 * * * *", Arguments: "{\"source\":"),
            new TestSchedule("twice", "0 * * * *"),
            new TestSchedule("twice", "30 * * * *"));
        await using var _ = app;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => app.GetRequiredService<Scheduler>().RunDueAsync(TestContext.Current.CancellationToken));

        Assert.Contains("'bad-cron' has an invalid cron expression", error.Message);
        Assert.Contains("'bad-zone' has an unknown time zone", error.Message);
        Assert.Contains("'unknown-command' sends 'refresh-everything', which no module registered", error.Message);
        Assert.Contains("'wrong-module' sends 'refresh-catalog' to module 'Billing'", error.Message);
        Assert.Contains("'bad-arguments' has arguments that do not fit", error.Message);
        Assert.Contains("'twice' is configured 2 times", error.Message);
    }

    [Fact]
    public async Task WithoutDockerTheDatabaseTestsFailAndSayDockerIsNeeded()
    {
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => TestApp.StartOrExplainAsync(() => Task.FromException(new InvalidOperationException("Docker is not running."))));

        Assert.StartsWith("The database tests need Docker", failure.Message);
    }

    private static async Task<(ServiceProvider App, FakeTimeProvider Time, Refreshes Refreshes)> StartAsync(params TestSchedule[] schedules)
    {
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        return (TestApp.Build(await TestApp.CreateDatabaseAsync(), time, refreshes, enabled: true, schedules), time, refreshes);
    }

    private static async Task<DateTimeOffset?> LastFiredAsync(IServiceProvider app, string name)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT last_fired_at FROM scheduling.schedules WHERE name = @name", connection);
        command.Parameters.AddWithValue("name", name);
        return await command.ExecuteScalarAsync(TestContext.Current.CancellationToken) is DateTime value ? new DateTimeOffset(value, TimeSpan.Zero) : null;
    }

    private static async Task WaitUntilBlockedOnALockAsync(NpgsqlDataSource dataSource)
    {
        while (true)
        {
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'");
            if ((long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))! > 0)
            {
                return;
            }

            await Task.Yield();
        }
    }

    private static async Task<long> CountSchedulesAsync(IServiceProvider app)
    {
        await using var connection = await app.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM scheduling.schedules", connection);
        return (long)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
    }
}
