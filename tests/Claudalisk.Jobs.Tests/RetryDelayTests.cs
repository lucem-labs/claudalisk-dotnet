using Claudalisk.Dispatching;
using Claudalisk.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs.Tests;

public sealed class RetryDelayTests
{
    private static JobRunner RunnerWith(Action<JobsOptions> configure)
    {
        var options = new JobsOptions { RetryBaseDelay = TimeSpan.Zero };
        configure(options);
        return new JobRunner(null!, null!, new JobsRegistry(), Options.Create(options), NullLogger<JobRunner>.Instance, TimeProvider.System, null);
    }

    [Fact]
    public void AWaitLongerThanTheRetryAfterLimitIsClampedToIt()
    {
        var runner = RunnerWith(options => options.RetryAfterMaxDelay = TimeSpan.FromMinutes(30));

        var delay = runner.RetryDelay(new DependencyUnavailableException("down", TimeSpan.FromDays(2)), 1);

        Assert.Equal(TimeSpan.FromMinutes(30), delay);
    }

    [Fact]
    public void AWaitWithinTheRetryAfterLimitIsKept()
    {
        var runner = RunnerWith(_ => { });

        var delay = runner.RetryDelay(new DependencyUnavailableException("down", TimeSpan.FromMinutes(10)), 1);

        Assert.Equal(TimeSpan.FromMinutes(10), delay);
    }

    [Fact]
    public void TheBackoffIsStillBoundByTheRetryMaxDelay()
    {
        var runner = RunnerWith(options =>
        {
            options.RetryBaseDelay = TimeSpan.FromMinutes(10);
            options.RetryMaxDelay = TimeSpan.FromMinutes(5);
        });

        var delay = runner.RetryDelay(new DependencyUnavailableException("down", TimeSpan.FromSeconds(1)), 6);

        Assert.Equal(TimeSpan.FromMinutes(5), delay);
    }

    [Fact]
    public void TheRetryAfterLimitMustBePositive()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.RetryAfterMaxDelay = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JobsOptions>>().Value);

        Assert.Contains("RetryAfterMaxDelay", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(nameof(JobsOptions.HeartbeatInterval))]
    [InlineData(nameof(JobsOptions.StaleAfter))]
    [InlineData(nameof(JobsOptions.RetryMaxDelay))]
    public void EveryJobsIntervalMustBePositive(string name)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => typeof(JobsOptions).GetProperty(name)!.SetValue(options, TimeSpan.Zero));
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JobsOptions>>().Value);

        Assert.Contains(name, failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void ThePollIntervalMustBePositiveOrInfinite(int seconds)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.PollInterval = TimeSpan.FromSeconds(seconds));
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JobsOptions>>().Value);

        Assert.Contains("PollInterval", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnInfinitePollIntervalIsValid()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.PollInterval = Timeout.InfiniteTimeSpan);
        using var provider = services.BuildServiceProvider();

        Assert.Equal(Timeout.InfiniteTimeSpan, provider.GetRequiredService<IOptions<JobsOptions>>().Value.PollInterval);
    }

    [Fact]
    public void TheRetryBaseDelayMustNotBeNegative()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.RetryBaseDelay = TimeSpan.FromSeconds(-1));
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JobsOptions>>().Value);

        Assert.Contains("RetryBaseDelay", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void InvalidJobsOptionsFailTheStartupValidation()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddClaudaliskDispatching();
        services.AddClaudaliskJobs(options => options.PollInterval = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();

        Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IStartupValidator>().Validate());
    }
}
