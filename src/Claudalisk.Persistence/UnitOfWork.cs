using System.Data;

namespace Claudalisk.Persistence;

internal sealed class UnitOfWork(DbSession session) : IUnitOfWork
{
    public bool IsActive => session.Transaction is not null;

    public bool HasForeignTransaction => session.Transaction is null && session.HasContextOwnedTransaction;

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (session.Transaction is not null)
        {
            return await work(cancellationToken).ConfigureAwait(false);
        }

        if (session.Connection.State != ConnectionState.Open)
        {
            await session.Connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var transaction = (await session.Connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false));
        await using var transactionDisposal = transaction.ConfigureAwait(false);
        session.Transaction = transaction;
        try
        {
            var result = await work(cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return result;
        }
        finally
        {
            session.Transaction = null;
            session.ReleaseEnlistedContexts(transaction);
        }
    }
}
