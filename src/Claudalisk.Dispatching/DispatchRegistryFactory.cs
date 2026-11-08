using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class DispatchRegistryFactory(IServiceCollection services)
{
    private readonly Lock _gate = new();
    private IServiceCollection? _services = services;
    private ServiceDescriptor[]? _snapshot;

    public DispatchRegistry Create(IServiceProvider provider)
    {
        ServiceDescriptor[] snapshot;
        lock (_gate)
        {
            if (_services is not null)
            {
                _snapshot = [.. _services];
                _services = null;
            }

            snapshot = _snapshot!;
        }

        return new DispatchRegistry(snapshot, provider);
    }
}
