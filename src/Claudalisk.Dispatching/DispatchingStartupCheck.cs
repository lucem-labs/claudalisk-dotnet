using Microsoft.Extensions.Hosting;

namespace Claudalisk.Dispatching;

internal sealed class DispatchingStartupCheck(DispatchRegistry registry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        GC.KeepAlive(registry);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
