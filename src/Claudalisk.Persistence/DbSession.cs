using Npgsql;

namespace Claudalisk.Persistence;

internal sealed class DbSession(NpgsqlDataSource dataSource) : IScopeConnection, IDisposable, IAsyncDisposable
{
    public NpgsqlConnection Connection { get; } = dataSource.CreateConnection();

    public NpgsqlTransaction? Transaction { get; set; }

    public HashSet<NpgsqlTransaction> FinishedTransactions { get; } = [];

    public void Dispose() => Connection.Dispose();

    public ValueTask DisposeAsync() => Connection.DisposeAsync();
}
