using Npgsql;

namespace Claudalisk.Persistence;

public interface IScopeConnection
{
    NpgsqlConnection Connection { get; }

    NpgsqlTransaction? Transaction { get; }
}
