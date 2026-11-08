using Claudalisk.Dispatching;
using Claudalisk.Modules;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs;

public static class JobsServiceCollectionExtensions
{
    public static IServiceCollection AddClaudaliskModuleJobs(this IServiceCollection services, string module, string schema, Action<ModuleJobsBuilder> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        var registry = Registry(services);
        if (registry.Find(module) is { LibraryOwned: true } owned)
        {
            throw new InvalidOperationException($"The jobs module name '{owned.Name}' is reserved by a library. Rename that jobs module.");
        }

        configure(new ModuleJobsBuilder(registry.GetOrAdd(module, schema), services, registry));
        return services;
    }

    internal static IServiceCollection AddLibraryJobs(this IServiceCollection services, string module, string schema, Action<ModuleJobsBuilder> configure)
    {
        var registry = Registry(services);
        configure(new ModuleJobsBuilder(registry.GetOrAdd(module, schema), services, registry).LibraryOwned());
        return services;
    }

    public static IServiceCollection AddClaudaliskJobs(this IServiceCollection services, Action<JobsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        if (configure is not null)
        {
            services.Configure(configure);
        }

        if (services.Any(descriptor => descriptor.ServiceType == typeof(JobRunner)))
        {
            return services;
        }

        if (!services.Any(descriptor => descriptor.ServiceType == typeof(IDispatcher)))
        {
            throw new InvalidOperationException("Call AddClaudaliskDispatching before AddClaudaliskJobs: jobs complete inside the dispatcher's unit-of-work step.");
        }

        Registry(services);
        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<JobsOptions>()
            .Validate(
                options => options.CancelCheckInterval > TimeSpan.Zero && options.PruneInterval > TimeSpan.Zero && options.FinishedJobRetention > TimeSpan.Zero
                    && options.RetryAfterMaxDelay > TimeSpan.Zero && options.HeartbeatInterval > TimeSpan.Zero
                    && options.StaleAfter > TimeSpan.Zero && options.StaleSweepInterval > TimeSpan.Zero && options.RetryMaxDelay > TimeSpan.Zero,
                "JobsOptions.CancelCheckInterval, PruneInterval, FinishedJobRetention, RetryAfterMaxDelay, HeartbeatInterval, StaleAfter, StaleSweepInterval and RetryMaxDelay must be positive.")
            .Validate(options => options.PollInterval > TimeSpan.Zero || options.PollInterval == Timeout.InfiniteTimeSpan, "JobsOptions.PollInterval must be positive or infinite.")
            .Validate(options => options.RetryBaseDelay >= TimeSpan.Zero, "JobsOptions.RetryBaseDelay must not be negative.")
            .ValidateOnStart();
        services.TryAddScoped<JobContext>();
        services.TryAddScoped<IJobContext>(provider => provider.GetRequiredService<JobContext>());
        services.TryAddScoped<IJobQueue, JobQueue>();
        services.TryAddScoped<IJobHistory, JobHistory>();
        services.TryAddScoped<AggregateTracker>();
        services.TryAddScoped<IAggregateTracker>(provider => provider.GetRequiredService<AggregateTracker>());
        services.TryAddScoped<EventPublisher>();
        services.TryAddScoped<IEventPublisher>(provider => provider.GetRequiredService<EventPublisher>());
        services.AddScoped<IPipelineStep, JobsPipelineStep>();
        services.AddSingleton(provider => new JobRunner(
            provider.GetRequiredService<IServiceScopeFactory>(),
            provider.GetRequiredService<IJobTransport>(),
            provider.GetRequiredService<JobsRegistry>(),
            provider.GetRequiredService<IOptions<JobsOptions>>(),
            provider.GetRequiredService<ILogger<JobRunner>>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetService<ModuleCatalog>()));
        services.AddHostedService<JobsModulesCheck>();
        services.AddHostedService<JobWorkers>();
        return services;
    }

    private static JobsRegistry Registry(IServiceCollection services)
    {
        var registry = services.Select(descriptor => descriptor.ImplementationInstance).OfType<JobsRegistry>().SingleOrDefault();
        if (registry is null)
        {
            registry = new JobsRegistry();
            services.AddSingleton(registry);
        }

        return registry;
    }
}
