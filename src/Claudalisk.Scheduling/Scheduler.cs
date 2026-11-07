using Claudalisk.Dispatching;
using Claudalisk.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Scheduling;

public sealed partial class Scheduler
{
    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _time;
    private readonly ILogger<Scheduler> _logger;
    private readonly Lazy<IReadOnlyList<Schedule>> _schedules;

    internal Scheduler(IServiceScopeFactory scopes, IOptions<SchedulingOptions> options, SchedulableCommands commands, TimeProvider time, ILogger<Scheduler> logger)
    {
        _scopes = scopes;
        _time = time;
        _logger = logger;
        _schedules = new Lazy<IReadOnlyList<Schedule>>(() => Schedule.From(options.Value, commands));
    }

    public IReadOnlyList<string> Schedules => _schedules.Value.Select(schedule => schedule.Name).ToArray();

    public async Task<IReadOnlyList<string>> RunDueAsync(CancellationToken cancellationToken = default)
    {
        var fired = new List<string>();
        foreach (var schedule in _schedules.Value)
        {
            if (await ClaimAsync(schedule, _time.GetUtcNow(), cancellationToken) is not { } occurrence)
            {
                continue;
            }

            fired.Add(schedule.Name);
            try
            {
                await using var scope = _scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(schedule.Message, cancellationToken);
                LogSent(_logger, schedule.Name, schedule.Command.Name, schedule.Command.Module, occurrence);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogSendFailed(_logger, exception, schedule.Name, schedule.Command.Name, occurrence);
            }
        }

        return fired;
    }

    private async Task<DateTimeOffset?> ClaimAsync(Schedule schedule, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var scope = _scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SchedulingDbContext>();
        return await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync<DateTimeOffset?>(async token =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO scheduling.schedules (name, last_fired_at) VALUES ({schedule.Name}, NULL) ON CONFLICT (name) DO NOTHING", token);
            var rows = await db.Database
                .SqlQuery<DateTimeOffset?>($"SELECT last_fired_at AS \"Value\" FROM scheduling.schedules WHERE name = {schedule.Name} FOR UPDATE")
                .ToListAsync(token);
            if (rows.Single() is not { } last)
            {
                await SetLastFiredAsync(db, schedule, now, token);
                return null;
            }

            if (schedule.NextAfter(last) is not { } next || next > now || schedule.LatestDue(last, now) is not { } occurrence)
            {
                return null;
            }

            await SetLastFiredAsync(db, schedule, occurrence, token);
            return occurrence;
        }, cancellationToken);
    }

    private static Task<int> SetLastFiredAsync(SchedulingDbContext db, Schedule schedule, DateTimeOffset at, CancellationToken cancellationToken) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE scheduling.schedules SET last_fired_at = {at} WHERE name = {schedule.Name}", cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Schedule {Schedule} sent {Command} to {Module} for {Occurrence}")]
    private static partial void LogSent(ILogger logger, string schedule, string command, string module, DateTimeOffset occurrence);

    [LoggerMessage(Level = LogLevel.Error, Message = "Schedule {Schedule} failed to send {Command} for {Occurrence}")]
    private static partial void LogSendFailed(ILogger logger, Exception exception, string schedule, string command, DateTimeOffset occurrence);
}
