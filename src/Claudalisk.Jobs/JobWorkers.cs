using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs;

internal sealed partial class JobWorkers(JobRunner runner, IJobTransport transport, JobsRegistry registry, IOptions<JobsOptions> options, ILogger<JobWorkers> logger)
    : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.WorkersEnabled)
        {
            return Task.CompletedTask;
        }

        var workers = registry.Modules
            .SelectMany(module => module.Lanes.SelectMany(lane => Enumerable.Range(0, lane.Value).Select(_ => WorkAsync(module, lane.Key, stoppingToken))))
            .ToArray();
        return Task.WhenAll(workers);
    }

    private async Task WorkAsync(JobsModule module, string lane, CancellationToken stoppingToken)
    {
        await Task.Yield();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await runner.RunNextAsync(module.Name, lane, stoppingToken))
                {
                    await transport.WaitForWorkAsync(module, lane, options.Value.PollInterval, stoppingToken);
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
                    await Task.Delay(options.Value.PollInterval, stoppingToken);
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
}
