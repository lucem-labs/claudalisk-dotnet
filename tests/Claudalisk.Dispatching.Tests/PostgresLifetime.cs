using Xunit;

[assembly: AssemblyFixture(typeof(Claudalisk.Dispatching.Tests.PostgresLifetime))]

namespace Claudalisk.Dispatching.Tests;

public sealed class PostgresLifetime : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => PostgresServer.DisposeContainerAsync();
}
