using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules;

public static class ModuleServiceCollectionExtensions
{
    public static IMvcBuilder AddModules(this IServiceCollection services, IConfiguration configuration, params IModule[] modules)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(modules);

        var catalog = services
            .Where(descriptor => descriptor.ServiceType == typeof(ModuleCatalog))
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ModuleCatalog>()
            .SingleOrDefault();
        var available = catalog is null ? new Dictionary<Type, IModule>() : new Dictionary<Type, IModule>(catalog.Registered);
        foreach (var module in modules)
        {
            ArgumentNullException.ThrowIfNull(module);
            available.TryAdd(module.GetType(), module);
        }

        var ordered = ModuleOrder.Resolve(available.Values, catalog?.Registered.Keys.ToHashSet() ?? []);

        if (catalog is null)
        {
            catalog = new ModuleCatalog(services.AddControllers());
            services.AddSingleton(catalog);
        }

        foreach (var module in ordered)
        {
            module.ConfigureServices(services, configuration);
            var assembly = module.GetType().Assembly;
            if (!catalog.Mvc.PartManager.ApplicationParts.OfType<AssemblyPart>().Any(part => part.Assembly == assembly))
            {
                catalog.Mvc.AddApplicationPart(assembly);
            }
            catalog.Add(module);
        }

        return catalog.Mvc;
    }
}
