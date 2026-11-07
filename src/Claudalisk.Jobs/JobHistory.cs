namespace Claudalisk.Jobs;

internal sealed class JobHistory(JobsRegistry registry, IJobTransport transport, IJobWriter writer) : IJobHistory
{
    public Task<JobHistoryPage> ListAsync(string module, JobHistoryQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.Page, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(query.PageSize, 1);
        return transport.ListAsync(Find(module), query, cancellationToken);
    }

    public Task<bool> DeleteFinishedAsync(string module, Guid id, CancellationToken cancellationToken = default) =>
        writer.DeleteFinishedAsync(Find(module), id, cancellationToken);

    private JobsModule Find(string module) =>
        registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
}
