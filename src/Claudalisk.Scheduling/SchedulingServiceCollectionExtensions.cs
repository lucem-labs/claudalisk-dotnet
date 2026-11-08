using Claudalisk.Jobs;
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
        AddSchedulingJobs(services, jobs => jobs.Command<TCommand>(name));
        return services;
    }

    public static IServiceCollection AddScheduling(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        if (services.Any(descriptor => descriptor.ServiceType == typeof(Scheduler)))
        {
            return services;
        }

        var commands = Commands(services);
        services.AddOptions<SchedulingOptions>().Bind(configuration.GetSection(SchedulingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<SchedulingOptions>>(new SchedulingOptionsValidator(commands));
        AddSchedulingJobs(services, _ => { });
        services.TryAddSingleton(TimeProvider.System);
        services.AddModuleDbContext<SchedulingDbContext>(SchedulingDbContext.Schema);
        services.AddSingleton(provider => new Scheduler(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<JobsRegistry>(),
            provider.GetRequiredService<IOptions<SchedulingOptions>>(),
            provider.GetRequiredService<SchedulableCommands>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<Scheduler>>()));
        services.AddHostedService<SchedulingStartupCheck>();
        services.AddHostedService<SchedulerService>();
        return services;
    }

    private static void AddSchedulingJobs(IServiceCollection services, Action<ModuleJobsBuilder> configure)
    {
        var registry = services.Select(descriptor => descriptor.ImplementationInstance).OfType<JobsRegistry>().SingleOrDefault();
        if (registry?.Find(SchedulingJobs.Name) is { LibraryOwned: false })
        {
            throw new InvalidOperationException(
                $"The jobs module name '{SchedulingJobs.Name}' is reserved for the scheduler, but AddModuleJobs already registered a jobs module with that name. Rename that jobs module.");
        }

        services.AddModuleJobs(SchedulingJobs.Name, SchedulingJobs.Schema, jobs => configure(jobs.LibraryOwned()));
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
