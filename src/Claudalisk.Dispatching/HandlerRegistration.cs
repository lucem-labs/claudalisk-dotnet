using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed record HandlerRegistration(Type MessageType, Type ResultType, Type ServiceType, Type? ImplementationType, ServiceLifetime Lifetime)
{
    public static HandlerRegistration? From(ServiceDescriptor descriptor)
    {
        if (!descriptor.ServiceType.IsGenericType || descriptor.ServiceType.GetGenericTypeDefinition() != typeof(IHandler<,>))
        {
            return null;
        }

        var arguments = descriptor.ServiceType.GetGenericArguments();
        var implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
        return new HandlerRegistration(arguments[0], arguments[1], descriptor.ServiceType, implementation ?? descriptor.ImplementationInstance?.GetType(), descriptor.Lifetime);
    }

    public string Name => (ImplementationType ?? ServiceType).FullName!;
}
