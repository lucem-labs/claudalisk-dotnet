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
        return new JobRunner(null!, null!, new JobsRegistry(), Options.Create(options), NullLogger<JobRunner>.Instance, TimeProvider.System);
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
        services.AddDispatching();
        services.AddJobs(options => options.RetryAfterMaxDelay = TimeSpan.Zero);
        using var provider = services.BuildServiceProvider();

        var failure = Assert.Throws<OptionsValidationException>(() => provider.GetRequiredService<IOptions<JobsOptions>>().Value);

        Assert.Contains("RetryAfterMaxDelay", failure.Message, StringComparison.Ordinal);
    }
}
