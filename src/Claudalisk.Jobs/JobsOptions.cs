namespace Claudalisk.Jobs;

public sealed class JobsOptions
{
    public bool WorkersEnabled { get; set; } = true;

    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan HeartbeatInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan CancelCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan FinishedJobRetention { get; set; } = TimeSpan.FromDays(30);

    public TimeSpan PruneInterval { get; set; } = TimeSpan.FromHours(1);

    public TimeSpan StaleAfter { get; set; } = TimeSpan.FromMinutes(2);

    public TimeSpan StaleSweepInterval { get; set; } = TimeSpan.FromMinutes(1);

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan RetryMaxDelay { get; set; } = TimeSpan.FromMinutes(5);

    public TimeSpan RetryAfterMaxDelay { get; set; } = TimeSpan.FromHours(1);
}
