using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Claudalisk.Dispatching;

internal sealed class DispatchingStartupCheck(IServiceProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        provider.ValidateDispatching();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
