using Cronos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Npgsql;

namespace Claudalisk.Scheduling.Tests;

public sealed class CatchUpTests
{
    private static (Schedule Schedule, Func<int> Calls) Counting(string cron)
    {
        var calls = 0;
        var schedule = new Schedule(
            "counted",
            CronExpression.Parse(cron, CronFormat.Standard),
            TimeZoneInfo.Utc,
            new SchedulableCommand("refresh-catalog", "Catalog", typeof(RefreshCatalogCommand)),
            new RefreshCatalogCommand("x"))
        {
            Occurrence = (expression, after, timeZone) =>
            {
                calls++;
                return expression.GetNextOccurrence(after, timeZone);
            },
        };
        return (schedule, () => calls);
    }

    [Fact]
    public void AMinutelyScheduleThirtyDaysBehindFindsTheLatestMinuteWithFewCronCalls()
    {
        var (schedule, calls) = Counting("* * * * *");
        var now = new DateTimeOffset(2026, 10, 26, 10, 30, 20, TimeSpan.Zero);

        var latest = schedule.LatestDue(now.AddDays(-30), now);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 10, 30, 0, TimeSpan.Zero), latest);
        Assert.InRange(calls(), 1, 4);
    }

    [Fact]
    public void AnHourlyScheduleThirtyDaysBehindFindsTheLatestHourWithABoundedNumberOfCronCalls()
    {
        var (schedule, calls) = Counting("0 * * * *");
        var now = new DateTimeOffset(2026, 10, 26, 10, 30, 20, TimeSpan.Zero);

        var latest = schedule.LatestDue(now.AddDays(-30), now);

        Assert.Equal(new DateTimeOffset(2026, 10, 26, 10, 0, 0, TimeSpan.Zero), latest);
        Assert.InRange(calls(), 1, 20);
    }

    [Fact]
    public void NothingIsDueWhenTheNextOccurrenceIsInTheFuture()
    {
        var (schedule, _) = Counting("0 * * * *");
        var now = new DateTimeOffset(2026, 10, 26, 10, 30, 20, TimeSpan.Zero);

        Assert.Null(schedule.LatestDue(now.AddMinutes(-5), now));
    }

    [Fact]
    public void ADueOccurrenceExactlyAtNowIsTheLatest()
    {
        var (schedule, _) = Counting("0 * * * *");
        var now = new DateTimeOffset(2026, 10, 26, 11, 0, 0, TimeSpan.Zero);

        Assert.Equal(now, schedule.LatestDue(now.AddHours(-5), now));
    }

    [Fact]
    public async Task ADailyScheduleFiresOnceWhenTheSpringForwardHourIsSkipped()
    {
        var (app, time, refreshes) = await StartAsync(new DateTimeOffset(2027, 3, 27, 12, 0, 0, TimeSpan.Zero));
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        time.SetUtcNow(new DateTimeOffset(2027, 3, 28, 0, 59, 0, TimeSpan.Zero));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 3, 28, 1, 0, 0, TimeSpan.Zero));
        Assert.Equal(["warsaw-night"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 3, 28, 23, 0, 0, TimeSpan.Zero));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 3, 29, 0, 30, 0, TimeSpan.Zero));
        Assert.Equal(["warsaw-night"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        await SchedulerTests.RunJobsAsync(app);
        Assert.Equal(2, refreshes.Sources.Count);
    }

    [Fact]
    public async Task ADailyScheduleFiresOnceWhenTheFallBackHourRepeats()
    {
        var (app, time, refreshes) = await StartAsync(new DateTimeOffset(2027, 10, 30, 12, 0, 0, TimeSpan.Zero));
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        time.SetUtcNow(new DateTimeOffset(2027, 10, 31, 0, 29, 0, TimeSpan.Zero));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 10, 31, 0, 30, 0, TimeSpan.Zero));
        Assert.Equal(["warsaw-night"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 10, 31, 1, 30, 0, TimeSpan.Zero));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        time.SetUtcNow(new DateTimeOffset(2027, 11, 1, 1, 30, 0, TimeSpan.Zero));
        Assert.Equal(["warsaw-night"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        await SchedulerTests.RunJobsAsync(app);
        Assert.Equal(2, refreshes.Sources.Count);
    }

    [Fact]
    public async Task ADowntimeOfThirtyDaysFiresTheLatestMinuteOnce()
    {
        var (app, time, refreshes) = await StartAsync(TestApp.Start, new TestSchedule("every-minute", "* * * * *"));
        await using var _ = app;
        var scheduler = app.GetRequiredService<Scheduler>();
        await scheduler.RunDueAsync(TestContext.Current.CancellationToken);

        time.Advance(TimeSpan.FromDays(30).Add(TimeSpan.FromSeconds(20)));
        Assert.Equal(["every-minute"], await scheduler.RunDueAsync(TestContext.Current.CancellationToken));
        Assert.Empty(await scheduler.RunDueAsync(TestContext.Current.CancellationToken));

        await SchedulerTests.RunJobsAsync(app);
        Assert.Single(refreshes.Sources);
        await using var command = app.GetRequiredService<NpgsqlDataSource>().CreateCommand("SELECT dedupe_key FROM scheduling.jobs");
        Assert.Equal("every-minute:2026-11-25T10:30:00.0000000Z", (string)(await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))!);
    }

    private static async Task<(ServiceProvider App, FakeTimeProvider Time, Refreshes Refreshes)> StartAsync(DateTimeOffset start, TestSchedule? schedule = null)
    {
        var time = new FakeTimeProvider(start);
        var refreshes = new Refreshes();
        var app = TestApp.Build(
            await TestApp.CreateDatabaseAsync(), time, refreshes, enabled: true, schedule ?? new TestSchedule("warsaw-night", "30 2 * * *", TimeZone: "Europe/Warsaw"));
        return (app, time, refreshes);
    }
}
