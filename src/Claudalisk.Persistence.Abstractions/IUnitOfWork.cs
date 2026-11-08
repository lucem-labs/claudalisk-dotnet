namespace Claudalisk.Persistence;

public interface IUnitOfWork
{
    bool IsActive { get; }

    Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default);

    Task RunAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        return RunAsync<bool>(async token =>
        {
            await work(token).ConfigureAwait(false);
            return true;
        }, cancellationToken);
    }
}
