using Claudalisk.Dispatching;
using Claudalisk.Jobs;
using Claudalisk.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace Claudalisk.Scheduling.Tests;

public sealed class StartupTests
{
    private const string Connection = "Host=localhost";

    private static ServiceProvider Build(TestSettings settings, params TestSchedule[] schedules) =>
        TestApp.Services(Connection, new FakeTimeProvider(TestApp.Start), new Refreshes(), settings, schedules).BuildServiceProvider();

#if NET11_0_OR_GREATER
    private static async Task ValidateStartupAsync(ServiceProvider app) =>
        await app.GetRequiredService<IAsyncStartupValidator>().ValidateAsync(TestContext.Current.CancellationToken);
#else
    private static Task ValidateStartupAsync(ServiceProvider app)
    {
        app.GetRequiredService<IStartupValidator>().Validate();
        return Task.CompletedTask;
    }
#endif

    [Fact]
    public async Task AnInvalidScheduleFailsStartupEvenWhenTheSchedulerIsDisabled()
    {
        await using var app = Build(new TestSettings(Enabled: false), new TestSchedule("bad-cron", "every hour"));

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => ValidateStartupAsync(app));

        Assert.Contains("'bad-cron' has an invalid cron expression", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("00:00:00")]
    [InlineData("-00:00:05")]
    public async Task ATickIntervalThatIsNotPositiveFailsStartup(string tickInterval)
    {
        await using var app = Build(new TestSettings(TickInterval: tickInterval));

        var failure = await Assert.ThrowsAsync<OptionsValidationException>(() => ValidateStartupAsync(app));

        Assert.Contains("TickInterval must be positive", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidSchedulesPassStartupValidation()
    {
        await using var app = Build(new TestSettings(), new TestSchedule("hourly-refresh", "0 * * * *"));

        await ValidateStartupAsync(app);
    }

    [Fact]
    public async Task AddingTheSchedulerTwiceKeepsOneCopyOfEverySchedule()
    {
        var services = TestApp.Services(Connection, new FakeTimeProvider(TestApp.Start), new Refreshes(), new TestSettings(), new TestSchedule("hourly-refresh", "0 * * * *"));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Scheduling:Schedules:0:Name"] = "hourly-refresh" }).Build();
        services.AddClaudaliskScheduling(configuration);
        await using var app = services.BuildServiceProvider();

        await ValidateStartupAsync(app);

        Assert.Equal(["hourly-refresh"], app.GetRequiredService<Scheduler>().Schedules);
    }

    [Fact]
    public async Task ASchedulableCommandOfAnUnknownModuleFailsStartupNamingTheModules()
    {
        var services = TestApp.Services(Connection, new FakeTimeProvider(TestApp.Start), new Refreshes(), new TestSettings());
        services.AddSchedulableCommand<RebuildIndexCommand>("Ctalog", "typo");
        await using var app = services.BuildServiceProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetServices<IHostedService>().Single(service => service is SchedulingStartupCheck).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("'typo' of module 'Ctalog'", failure.Message, StringComparison.Ordinal);
        Assert.Contains("'Catalog'", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJobsModuleWithTheSchedulersNameFailsAddScheduling()
    {
        var services = new ServiceCollection();
        services.AddClaudaliskModuleJobs("Scheduling", "app_scheduling", _ => { });

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddClaudaliskScheduling(new ConfigurationBuilder().Build()));

        Assert.Contains("'Scheduling' is reserved", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AJobsModuleWithTheSchedulersNameFailsAddModuleJobsAfterAddScheduling()
    {
        var services = new ServiceCollection();
        services.AddClaudaliskScheduling(new ConfigurationBuilder().Build());

        var failure = Assert.Throws<InvalidOperationException>(() => services.AddClaudaliskModuleJobs("Scheduling", "app_scheduling", _ => { }));

        Assert.Contains("'Scheduling' is reserved", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SchedulingWithoutJobsFailsStartupNamingWhatIsMissing()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskPersistence(Connection);
        services.AddClaudaliskDispatching();
        services.AddClaudaliskScheduling(new ConfigurationBuilder().Build());
        await using var app = services.BuildServiceProvider();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => app.GetServices<IHostedService>().Single(service => service is SchedulingStartupCheck).StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("AddClaudaliskJobs", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSchedulerIsDisabledUnlessConfiguredAndNothingFiresAsTimePasses()
    {
        var connection = await TestApp.CreateDatabaseAsync();
        var time = new FakeTimeProvider(TestApp.Start);
        var refreshes = new Refreshes();
        await using var app = TestApp.Services(connection, time, refreshes, new TestSettings(Enabled: null, Workers: true), new TestSchedule("hourly-refresh", "0 * * * *"))
            .BuildServiceProvider();
        Assert.False(app.GetRequiredService<IOptions<SchedulingOptions>>().Value.Enabled);
        var hosted = app.GetServices<IHostedService>().ToArray();
        foreach (var service in hosted)
        {
            await service.StartAsync(TestContext.Current.CancellationToken);
        }

        await Assert.Single(hosted.OfType<SchedulerService>()).ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromHours(5));
        foreach (var service in hosted)
        {
            await service.StopAsync(TestContext.Current.CancellationToken);
        }

        Assert.Empty(refreshes.Sources);
    }
}
