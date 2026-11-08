using Xunit;

[assembly: AssemblyFixture(typeof(Claudalisk.Jobs.Tests.PostgresLifetime))]

namespace Claudalisk.Jobs.Tests;

public sealed class PostgresLifetime : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => PostgresServer.DisposeContainerAsync();
}
