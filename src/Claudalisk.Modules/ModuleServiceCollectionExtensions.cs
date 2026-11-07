using System.Reflection;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules;

public static class ModuleServiceCollectionExtensions
{
    public static IServiceCollection AddModules(this IServiceCollection services, IConfiguration configuration, params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var registry = FindRegistry(services);
        var registered = registry?.Modules ?? [];
        var registeredTypes = registered.Select(module => module.ModuleType).ToHashSet();
        var candidates = modules
            .Select(module => ModuleDeclaration.Read(module, nameof(modules)))
            .DistinctBy(module => module.ModuleType)
            .Where(module => !registeredTypes.Contains(module.ModuleType))
            .ToList();

        var ordered = ModuleOrder.Resolve(registered, candidates);

        if (registry is null)
        {
            registry = new ModuleRegistry();
            services.AddSingleton(registry);
        }

        if (ordered.Count == 0)
        {
            return services;
        }

        AddApplicationParts(services, registry, ordered.Select(module => module.ModuleType.Assembly).Distinct());

        foreach (var module in ordered)
        {
            module.Module.Register(new ModuleRegistration(services, configuration, module.Contract));
            registry.Add(new RegisteredModule(module.ModuleType, module.Contract, module.Dependencies));
        }

        return services;
    }

    private static ModuleRegistry? FindRegistry(IServiceCollection services) =>
        services
            .Where(descriptor => descriptor.ServiceType == typeof(ModuleRegistry) && !descriptor.IsKeyedService)
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ModuleRegistry>()
            .SingleOrDefault();

    private static void AddApplicationParts(IServiceCollection services, ModuleRegistry registry, IEnumerable<Assembly> assemblies)
    {
        if (!registry.ControllersAdded)
        {
            services.AddControllers();
            registry.ControllersAdded = true;
        }

        var manager = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(ApplicationPartManager))?.ImplementationInstance as ApplicationPartManager
            ?? throw new InvalidOperationException("ApplicationPartManager is registered without an instance, so the module assemblies cannot be added as application parts. Let AddControllers register it.");

        foreach (var assembly in assemblies)
        {
            var name = assembly.GetName().Name;
            if (manager.ApplicationParts.Any(part => part.Name == name))
            {
                continue;
            }

            foreach (var part in ApplicationPartFactory.GetApplicationPartFactory(assembly).GetApplicationParts(assembly))
            {
                manager.ApplicationParts.Add(part);
            }
        }
    }
}
