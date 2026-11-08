using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed record HandlerRegistration(Type MessageType, Type ResultType, Type ServiceType, Type? ImplementationType, ServiceLifetime Lifetime, bool IsKeyed = false, bool IsUnregistered = false)
{
    public static HandlerRegistration? From(ServiceDescriptor descriptor)
    {
        if (!descriptor.ServiceType.IsGenericType || descriptor.ServiceType.GetGenericTypeDefinition() != typeof(IHandler<,>))
        {
            return null;
        }

        var arguments = descriptor.ServiceType.GetGenericArguments();
        var implementation = descriptor.IsKeyedService ? descriptor.KeyedImplementationType : descriptor.ImplementationType;
        return new HandlerRegistration(
            arguments[0],
            arguments[1],
            descriptor.ServiceType,
            implementation ?? descriptor.ImplementationInstance?.GetType(),
            descriptor.Lifetime,
            descriptor.IsKeyedService);
    }

    public string Name => (ImplementationType ?? ServiceType).FullName!;

    public object Resolve(IServiceProvider scope) =>
        IsUnregistered
            ? ActivatorUtilities.CreateInstance(scope, ImplementationType!)
            : scope.GetRequiredService(ServiceType);

    public async Task<object?> InvokeAsync(
        IServiceProvider scope, Func<object, object, CancellationToken, Task<object?>> invoke, object message, CancellationToken cancellationToken)
    {
        var handler = Resolve(scope);
        try
        {
            return await invoke(handler, message, cancellationToken);
        }
        finally
        {
            if (IsUnregistered)
            {
                await Disposal.DisposeAsync(handler);
            }
        }
    }
}
