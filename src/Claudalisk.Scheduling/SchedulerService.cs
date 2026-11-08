using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Scheduling;

internal sealed partial class SchedulerService(Scheduler scheduler, IOptions<SchedulingOptions> options, TimeProvider time, ILogger<SchedulerService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        var announced = false;
        using var timer = new PeriodicTimer(options.Value.TickInterval, time);
        do
        {
            try
            {
                if (!announced)
                {
                    var schedules = scheduler.Schedules;
                    LogRunning(logger, schedules.Count, schedules);
                    announced = true;
                }

                await scheduler.RunDueAsync(stoppingToken);
            }
            catch (Exception exception) when (!stoppingToken.IsCancellationRequested)
            {
                LogTickFailed(logger, exception);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "The scheduler runs {Count} schedules: {Schedules}")]
    private static partial void LogRunning(ILogger logger, int count, IReadOnlyList<string> schedules);

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduler failed a tick; it tries again on the next one")]
    private static partial void LogTickFailed(ILogger logger, Exception exception);
}
