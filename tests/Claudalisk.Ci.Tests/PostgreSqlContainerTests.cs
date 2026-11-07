using Npgsql;
using Testcontainers.PostgreSql;

namespace Claudalisk.Ci.Tests;

public sealed class PostgreSqlContainerTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder("postgres:18-alpine").Build();

    public async ValueTask InitializeAsync() => await _container.StartAsync(TestContext.Current.CancellationToken);

    public async ValueTask DisposeAsync() => await _container.DisposeAsync();

    [Fact]
    public async Task ContainerRunsTheExpectedPostgreSqlMajorVersion()
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        await using var dataSource = NpgsqlDataSource.Create(_container.GetConnectionString());
        await using var command = dataSource.CreateCommand("select current_setting('server_version_num')::int / 10000");

        var majorVersion = await command.ExecuteScalarAsync(cancellationToken);

        Assert.Equal(18, majorVersion);
    }
}
