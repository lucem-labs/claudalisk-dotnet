using Xunit;

[assembly: AssemblyFixture(typeof(Claudalisk.Persistence.Tests.PostgresLifetime))]

namespace Claudalisk.Persistence.Tests;

public sealed class PostgresLifetime : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => PostgresServer.DisposeContainerAsync();
}
