using System.Reflection;
using Claudalisk.Modules;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Claudalisk.Dispatching;

public static class DispatchingServiceCollectionExtensions
{
    public static IServiceCollection AddDispatching(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        var state = services.Select(descriptor => descriptor.ImplementationInstance).OfType<DispatchingState>().SingleOrDefault();
        if (state is null)
        {
            state = new DispatchingState();
            services.AddSingleton(state);
            services.AddSingleton(_ => new DispatchRegistry(services));
            services.TryAddScoped<IDispatcher, Dispatcher>();
            services.AddScoped<IPipelineStep, LoggingStep>();
            services.AddScoped<IPipelineStep, ValidationStep>();
            services.AddScoped<IPipelineStep, UnitOfWorkStep>();
            services.AddHostedService<DispatchingStartupCheck>();
        }

        var modules = services.Select(descriptor => descriptor.ImplementationInstance).OfType<ModuleCatalog>().SelectMany(catalog => catalog.Modules);
        foreach (var assembly in modules.Select(module => module.Assembly).Distinct().ToArray())
        {
            if (state.Scanned.Add(assembly))
            {
                AddHandlersAndValidators(services, assembly);
            }
        }

        return services;
    }

    public static IServiceProvider ValidateDispatching(this IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.GetRequiredService<DispatchRegistry>();
        return provider;
    }

    private static void AddHandlersAndValidators(IServiceCollection services, Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition))
        {
            foreach (var contract in type.GetInterfaces().Where(contract => contract.IsGenericType))
            {
                var definition = contract.GetGenericTypeDefinition();
                if (definition == typeof(IHandler<,>) || definition == typeof(IValidator<>))
                {
                    services.TryAddEnumerable(ServiceDescriptor.Scoped(contract, type));
                }
            }
        }
    }

    private sealed class DispatchingState
    {
        public HashSet<Assembly> Scanned { get; } = [];
    }
}
