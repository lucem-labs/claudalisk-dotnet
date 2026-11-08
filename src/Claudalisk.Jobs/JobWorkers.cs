using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs;

internal sealed partial class JobWorkers(JobRunner runner, IJobTransport transport, JobsRegistry registry, IOptions<JobsOptions> options, TimeProvider time, ILogger<JobWorkers> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkersEnabled)
        {
            return Task.CompletedTask;
        }

        var workers = registry.Modules.SelectMany(module => new[] { PruneAsync(module, stoppingToken), SweepAsync(module, stoppingToken) })
            .Concat(registry.Modules.SelectMany(module => module.Lanes.SelectMany(lane => Enumerable.Range(0, lane.Value).Select(_ => WorkAsync(module, lane.Key, stoppingToken)))))
            .ToArray();
        return Task.WhenAll(workers);
    }

    private Task PruneAsync(JobsModule module, CancellationToken stoppingToken) =>
        RepeatAsync(module, options.Value.PruneInterval, LogPruneFailed, token => runner.PruneAsync(module.Name, token), stoppingToken);

    private Task SweepAsync(JobsModule module, CancellationToken stoppingToken) =>
        RepeatAsync(module, options.Value.StaleSweepInterval, LogSweepFailed, token => runner.SweepStaleAsync(module.Name, token), stoppingToken);

    private async Task RepeatAsync(JobsModule module, TimeSpan interval, Action<ILogger, Exception, string> logFailed, Func<CancellationToken, Task> work, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval, time);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                try
                {
                    await work(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stoppingToken.IsCancellationRequested)
                {
                    logFailed(logger, exception, module.Name);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    private async Task WorkAsync(JobsModule module, string lane, CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await runner.RunNextAsync(module.Name, lane, stoppingToken).ConfigureAwait(false))
                {
                    await transport.WaitForWorkAsync(module, lane, options.Value.PollInterval, stoppingToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogWorkerFailed(logger, exception, module.Name, lane);
                try
                {
                    await Task.Delay(options.Value.PollInterval, time, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }
    [LoggerMessage(Level = LogLevel.Error, Message = "The {Module} worker on lane {Lane} failed; it waits and tries again")]
    private static partial void LogWorkerFailed(ILogger logger, Exception exception, string module, string lane);

    [LoggerMessage(Level = LogLevel.Error, Message = "Deleting the finished {Module} jobs failed; it tries again on the next interval")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception, string module);

    [LoggerMessage(Level = LogLevel.Error, Message = "Sweeping the stale {Module} jobs failed; it tries again on the next interval")]
    private static partial void LogSweepFailed(ILogger logger, Exception exception, string module);
}
