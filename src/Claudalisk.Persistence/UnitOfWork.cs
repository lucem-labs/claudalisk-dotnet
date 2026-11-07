using System.Data;

namespace Claudalisk.Persistence;

internal sealed class UnitOfWork(DbSession session) : IUnitOfWork
{
    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(work);
        if (session.Transaction is not null)
        {
            return await work(cancellationToken);
        }

        if (session.Connection.State != ConnectionState.Open)
        {
            await session.Connection.OpenAsync(cancellationToken);
        }

        await using var transaction = await session.Connection.BeginTransactionAsync(cancellationToken);
        session.Transaction = transaction;
        try
        {
            var result = await work(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        finally
        {
            session.FinishedTransactions.Add(transaction);
            session.Transaction = null;
        }
    }
}
