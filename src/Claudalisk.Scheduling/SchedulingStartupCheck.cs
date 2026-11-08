using Claudalisk.AspNetCore;
using Claudalisk.Jobs;
using Claudalisk.Modules;
using Microsoft.Extensions.Hosting;

namespace Claudalisk.Scheduling;

internal sealed class SchedulingStartupCheck(SchedulableCommands commands, IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(JobRunner)) is null || services.GetService(typeof(IJobTransport)) is null)
        {
            throw new InvalidOperationException(
                "AddScheduling queues each fire as a job: call AddJobs and register a jobs transport, such as AddPostgreSqlJobs.");
        }

        if (services.GetService(typeof(ModuleCatalog)) is not ModuleCatalog catalog)
        {
            return Task.CompletedTask;
        }

        var known = catalog.Modules.Select(module => module.Name).ToArray();
        var unknown = commands.All
            .Where(command => !known.Any(name => NameSegments.Kebab(name) == NameSegments.Kebab(command.Module)))
            .Select(command => $"'{command.Name}' of module '{command.Module}'")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return unknown.Length == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"AddSchedulableCommand named modules that are not in the module catalog: {string.Join(", ", unknown)}. "
                + $"Known modules: {string.Join(", ", known.Order(StringComparer.Ordinal).Select(name => $"'{name}'"))}.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
