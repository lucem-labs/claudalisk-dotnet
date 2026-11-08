using System.Text.Json;
using Cronos;

namespace Claudalisk.Scheduling;

internal sealed record Schedule(string Name, CronExpression Cron, TimeZoneInfo TimeZone, SchedulableCommand Command, object Message)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Func<CronExpression, DateTime, TimeZoneInfo, DateTime?> Occurrence { get; init; } =
        static (cron, after, zone) => cron.GetNextOccurrence(after, zone);

    public DateTimeOffset? NextAfter(DateTimeOffset last) =>
        Occurrence(Cron, last.UtcDateTime, TimeZone) is { } next ? new DateTimeOffset(next, TimeSpan.Zero) : null;

    public DateTimeOffset? LatestDue(DateTimeOffset last, DateTimeOffset now)
    {
        var window = TimeSpan.FromMinutes(1);
        while (true)
        {
            var from = now - window;
            var exhausted = from <= last;
            if (exhausted)
            {
                from = last;
            }

            if (NextAfter(from) is { } first && first <= now)
            {
                var latest = first;
                while (NextAfter(latest) is { } following && following <= now)
                {
                    latest = following;
                }

                return latest;
            }

            if (exhausted)
            {
                return null;
            }

            window *= 2;
        }
    }

    public static IReadOnlyList<Schedule> From(SchedulingOptions options, SchedulableCommands commands)
    {
        var problems = new List<string>();
        var schedules = new List<Schedule>();
        foreach (var duplicate in options.Schedules.GroupBy(schedule => schedule.Name, StringComparer.Ordinal).Where(group => group.Count() > 1))
        {
            problems.Add($"- Schedule '{duplicate.Key}' is configured {duplicate.Count()} times.");
        }

        foreach (var configured in options.Schedules)
        {
            var label = string.IsNullOrWhiteSpace(configured.Name) ? "(unnamed)" : configured.Name;
            if (string.IsNullOrWhiteSpace(configured.Name))
            {
                problems.Add("- A schedule has no name.");
                continue;
            }

            CronExpression? cron = null;
            try
            {
                cron = CronExpression.Parse(configured.Cron, CronFormat.Standard);
            }
            catch (CronFormatException exception)
            {
                problems.Add($"- Schedule '{label}' has an invalid cron expression '{configured.Cron}': {exception.Message}");
            }

            TimeZoneInfo? zone = null;
            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(configured.TimeZone);
            }
            catch (Exception exception) when (exception is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                problems.Add($"- Schedule '{label}' has an unknown time zone '{configured.TimeZone}'.");
            }

            var command = commands.Find(configured.Command);
            if (command is null)
            {
                problems.Add($"- Schedule '{label}' sends '{configured.Command}', which no module registered with AddSchedulableCommand.");
            }
            else if (!string.Equals(command.Module, configured.Module, StringComparison.Ordinal))
            {
                problems.Add($"- Schedule '{label}' sends '{configured.Command}' to module '{configured.Module}', but the command belongs to module '{command.Module}'.");
            }

            object? message = null;
            if (command is not null)
            {
                try
                {
                    message = JsonSerializer.Deserialize(string.IsNullOrWhiteSpace(configured.Arguments) ? "{}" : configured.Arguments, command.CommandType, Json);
                }
                catch (JsonException exception)
                {
                    problems.Add($"- Schedule '{label}' has arguments that do not fit '{command.CommandType.Name}': {exception.Message}");
                }
            }

            if (cron is not null && zone is not null && command is not null && message is not null)
            {
                schedules.Add(new Schedule(configured.Name, cron, zone, command, message));
            }
        }

        return problems.Count == 0
            ? schedules
            : throw new InvalidOperationException("The schedules are not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
    }
}
