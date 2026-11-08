using Xunit;

[assembly: AssemblyFixture(typeof(Claudalisk.Scheduling.Tests.PostgresLifetime))]

namespace Claudalisk.Scheduling.Tests;

public sealed class PostgresLifetime : IAsyncLifetime
{
    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public ValueTask DisposeAsync() => TestApp.DisposeContainerAsync();
}
