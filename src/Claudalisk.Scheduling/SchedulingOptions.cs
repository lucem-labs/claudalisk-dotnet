namespace Claudalisk.Scheduling;

public sealed class SchedulingOptions
{
    public const string SectionName = "Scheduling";

    public bool Enabled { get; set; }

    public TimeSpan TickInterval { get; set; } = TimeSpan.FromSeconds(30);

    public List<ScheduleOptions> Schedules { get; } = [];
}

public sealed class ScheduleOptions
{
    public string Name { get; set; } = "";

    public string Cron { get; set; } = "";

    public string Command { get; set; } = "";

    public string Module { get; set; } = "";

    public string TimeZone { get; set; } = "UTC";

    public string Arguments { get; set; } = "{}";
}
