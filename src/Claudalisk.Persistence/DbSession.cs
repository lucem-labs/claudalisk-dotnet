using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace Claudalisk.Persistence;

internal sealed class DbSession(NpgsqlDataSource dataSource) : IScopeConnection, IDisposable, IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = dataSource.CreateConnection();

    public NpgsqlTransaction? Transaction { get; set; }

    public List<DbContext> EnlistedContexts { get; } = [];

    public List<DbContext> ContextsThatBeganTransactions { get; } = [];

    public bool HasContextOwnedTransaction
    {
        get
        {
            ContextsThatBeganTransactions.RemoveAll(context => !HasTransaction(context));
            return ContextsThatBeganTransactions.Count > 0;
        }
    }

    private static bool HasTransaction(DbContext context)
    {
        try
        {
            return context.Database.CurrentTransaction is not null;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void ReleaseEnlistedContexts(NpgsqlTransaction transaction)
    {
        foreach (var context in EnlistedContexts)
        {
            try
            {
                if (ReferenceEquals(context.Database.CurrentTransaction?.GetDbTransaction(), transaction))
                {
                    context.Database.UseTransaction(null);
                }
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
            }
        }

        EnlistedContexts.Clear();
    }

    public void Dispose() => Connection.Dispose();

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
