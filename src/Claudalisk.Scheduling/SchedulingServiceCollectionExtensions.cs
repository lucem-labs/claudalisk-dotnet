using Claudalisk.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Scheduling;

public static class SchedulingServiceCollectionExtensions
{
    public static IServiceCollection AddSchedulableCommand<TCommand>(this IServiceCollection services, string module, string name)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Commands(services).Add(new SchedulableCommand(name, module, typeof(TCommand)));
        return services;
    }

    public static IServiceCollection AddScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddOptions<SchedulingOptions>().Bind(configuration.GetSection(SchedulingOptions.SectionName));
        if (services.Any(descriptor => descriptor.ServiceType == typeof(Scheduler)))
        {
            return services;
        }

        Commands(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<SchedulingDbContext>(SchedulingDbContext.Schema);
        services.AddSingleton(provider => new Scheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IOptions<SchedulingOptions>>(),
            provider.GetRequiredService<SchedulableCommands>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Scheduler>>()));
        services.AddHostedService<SchedulerService>();
        return services;
    }

    private static SchedulableCommands Commands(IServiceCollection services)
    {
        var commands = services.Select(descriptor => descriptor.ImplementationInstance).OfType<SchedulableCommands>().SingleOrDefault();
        if (commands is null)
        {
            commands = new SchedulableCommands();
            services.AddSingleton(commands);
        }

        return commands;
    }
}
