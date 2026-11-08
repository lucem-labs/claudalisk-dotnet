using System.Globalization;
using System.Text.Json;
using Claudalisk.Jobs;
using Claudalisk.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Scheduling;

public sealed partial class Scheduler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly JobsRegistry _jobs;
    private readonly TimeProvider _time;
    private readonly ILogger<Scheduler> _logger;
    private readonly Lazy<IReadOnlyList<Schedule>> _schedules;
    private bool _pruned;

    internal Scheduler(IServiceScopeFactory scopes, JobsRegistry jobs, IOptions<SchedulingOptions> options, SchedulableCommands commands, TimeProvider time, ILogger<Scheduler> logger)
    {
        _scopes = scopes;
        _jobs = jobs;
        _time = time;
        _logger = logger;
        _schedules = new Lazy<IReadOnlyList<Schedule>>(() => Schedule.From(options.Value, commands));
    }

    public IReadOnlyList<string> Schedules => _schedules.Value.Select(schedule => schedule.Name).ToArray();

    public async Task<IReadOnlyList<string>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var schedules = _schedules.Value;
        await PruneRemovedOnceAsync(schedules, cancellationToken).ConfigureAwait(false);
        var fired = new List<string>();
        foreach (var schedule in schedules)
        {
            try
            {
                if (await ClaimAsync(schedule, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false) is { } occurrence)
                {
                    fired.Add(schedule.Name);
                    LogQueued(_logger, schedule.Name, schedule.Command.Name, schedule.Command.Module, occurrence);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogClaimFailed(_logger, exception, schedule.Name, schedule.Command.Name);
            }
        }

        return fired;
    }

    private async Task PruneRemovedOnceAsync(IReadOnlyList<Schedule> schedules, CancellationToken cancellationToken)
    {
        if (_pruned)
        {
            return;
        }

        try
        {
            var names = schedules.Select(schedule => schedule.Name).ToArray();
            var scope = _scopes.CreateAsyncScope();
            await using var scopeDisposal = scope.ConfigureAwait(false);
            var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
            var removed = await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM scheduling.schedules WHERE name <> ALL({names})", cancellationToken).ConfigureAwait(false);
            _pruned = true;
            if (removed > 0)
            {
                LogPruned(_logger, removed);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogPruneFailed(_logger, exception);
        }
    }

    private async Task<DateTimeOffset?> ClaimAsync(Schedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var scope = _scopes.CreateAsyncScope();
        await using var scopeDisposal = scope.ConfigureAwait(false);
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        var writer = scope.ServiceProvider.GetRequiredService<IJobWriter>();
        var jobs = _jobs.Find(SchedulingJobs.Name) ?? throw new InvalidOperationException("The scheduler's jobs module is not registered. Call AddScheduling.");
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync<DateTimeOffset?>(async token =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO scheduling.schedules (name, last_fired_timestamp) VALUES ({schedule.Name}, NULL) ON CONFLICT (name) DO NOTHING", token).ConfigureAwait(false);
            var rows = await db.Database
                .SqlQuery<DateTimeOffset?>($"SELECT last_fired_timestamp AS \"Value\" FROM scheduling.schedules WHERE name = {schedule.Name} FOR UPDATE")
                .ToListAsync(token).ConfigureAwait(false);
            if (rows.Single() is not { } last)
            {
                await SetLastFiredAsync(db, schedule, now, token).ConfigureAwait(false);
                return null;
            }

            if (schedule.LatestDue(last, now) is not { } occurrence)
            {
                return null;
            }

            await SetLastFiredAsync(db, schedule, occurrence, token).ConfigureAwait(false);
            var dedupeKey = $"{schedule.Name}:{occurrence.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)}";
            var payload = JsonSerializer.Serialize(schedule.Message, schedule.Command.CommandType, JobQueue.Json);
            await writer.InsertAsync(jobs, new NewJob(schedule.Command.Name, payload, new JobOptions(DedupeKey: dedupeKey)), token).ConfigureAwait(false);
            return occurrence;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task<int> SetLastFiredAsync(SchedulingDbContext db, Schedule schedule, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE scheduling.schedules SET last_fired_timestamp = {at} WHERE name = {schedule.Name}", cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {Schedule} queued {Command} for {Module} for {Occurrence}")]
    private static partial void LogQueued(ILogger logger, string schedule, string command, string module, DateTimeOffset occurrence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule {Schedule} failed to queue {Command}; it tries again on the next tick")]
    private static partial void LogClaimFailed(ILogger logger, Exception exception, string schedule, string command);

    [LoggerMessage(Level = LogLevel.Information, Message = "The scheduler removed the state of {Count} schedules that are no longer configured")]
    private static partial void LogPruned(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Error, Message = "The scheduler failed to remove the state of removed schedules; it tries again on the next tick")]
    private static partial void LogPruneFailed(ILogger logger, Exception exception);
}
