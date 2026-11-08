using System.Reflection;
using Claudalisk.Modules;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Claudalisk.Dispatching;

public static class DispatchingServiceCollectionExtensions
{
    public static IServiceCollection AddDispatching(this IServiceCollection services, Action<DispatchingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var state = services.Select(descriptor => descriptor.ImplementationInstance).OfType<DispatchingState>().SingleOrDefault();
        if (state is null)
        {
            state = new DispatchingState();
            services.AddSingleton(state);
            services.AddOptions<DispatchingOptions>()
                .Validate(options => options.ConcurrencyAttempts >= 1, "DispatchingOptions.ConcurrencyAttempts must be at least 1.")
                .ValidateOnStart();
            services.AddSingleton(new DispatchRegistryFactory(services));
            services.AddSingleton(provider => provider.GetRequiredService<DispatchRegistryFactory>().Create(provider));
            services.TryAddScoped<Dispatcher>();
            services.TryAddScoped<IDispatcher>(provider => provider.GetRequiredService<Dispatcher>());
            services.TryAddScoped<IOuterPipeline, OuterPipeline>();
            services.AddScoped<IPipelineStep, LoggingStep>();
            services.AddScoped<IPipelineStep, ValidationStep>();
            services.AddScoped<IPipelineStep, UnitOfWorkStep>();
            services.AddHostedService<DispatchingStartupCheck>();
        }

        if (configure is not null)
        {
            services.Configure(configure);
        }

        var modules = services.Select(descriptor => descriptor.ImplementationInstance).OfType<ModuleCatalog>().SelectMany(catalog => catalog.Modules);
        foreach (var assembly in modules.Select(module => module.Assembly).Distinct().ToArray())
        {
            if (!state.Scanned.Contains(assembly) && AssemblyTypes.TryLoad(assembly, exportedOnly: false, out var types, out _))
            {
                state.Scanned.Add(assembly);
                AddHandlersAndValidators(services, types);
            }
        }

        return services;
    }

    public static IServiceProvider ValidateDispatching(this IServiceProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        provider.GetRequiredService<DispatchRegistry>();
        using var scope = provider.CreateScope();
        PipelineSteps.RejectRetrySlot(scope.ServiceProvider.GetServices<IPipelineStep>());
        return provider;
    }

    private static void AddHandlersAndValidators(IServiceCollection services, IEnumerable<Type> types)
    {
        foreach (var type in types.Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition))
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
}
