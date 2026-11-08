namespace Claudalisk.Jobs;

internal sealed class JobContext(IJobTransport transport) : IJobContext
{
    private JobsModule? _module;

    public bool IsActive => _module is not null;

    public Guid JobId { get; private set; }

    public string Module => _module?.Name ?? throw NotInAJob();

    public int Attempt { get; private set; }

    public object? Command { get; private set; }

    public string? ResultLink { get; private set; }

    public bool Completed { get; set; }

    internal JobsModule JobsModule => _module ?? throw NotInAJob();

    public void Start(JobsModule module, ClaimedJob job, object payload)
    {
        _module = module;
        JobId = job.Id;
        Attempt = job.Attempts;
        Command = payload;
    }

    public void SetResult(string href)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        _ = JobsModule;
        ResultLink = href;
    }

    public async Task CompleteAsync(IJobWriter writer, CancellationToken cancellationToken)
    {
        if (!await writer.CompleteAsync(JobsModule, JobId, Attempt, ResultLink, cancellationToken))
        {
            throw new JobNoLongerRunningException(JobsModule.Name, JobId, Attempt);
        }
    }

    public Task ReportProgressAsync(double fraction, string? step, CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(fraction, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fraction, 1);
        return transport.ReportProgressAsync(JobsModule, JobId, fraction, step, cancellationToken);
    }

    private static InvalidOperationException NotInAJob() => new("This code is not running as a job.");
}
