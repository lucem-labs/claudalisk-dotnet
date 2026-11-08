using Claudalisk.Modules;
using Microsoft.Extensions.Hosting;

namespace Claudalisk.Jobs;

internal sealed class JobsModulesCheck(JobsRegistry registry, IServiceProvider services) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (services.GetService(typeof(ModuleCatalog)) is not ModuleCatalog catalog)
        {
            return Task.CompletedTask;
        }

        var known = catalog.Modules.Select(module => module.Name).ToArray();
        var unknown = registry.Modules
            .Where(jobs => !jobs.LibraryOwned && !known.Any(name => NameSegments.Kebab(name) == jobs.Segment))
            .Select(jobs => $"'{jobs.Name}'")
            .Order(StringComparer.Ordinal)
            .ToArray();
        return unknown.Length == 0
            ? Task.CompletedTask
            : throw new InvalidOperationException(
                $"AddModuleJobs registered jobs for {string.Join(", ", unknown)}, which {(unknown.Length == 1 ? "is" : "are")} not in the module catalog. "
                + $"The jobs module name must be the name of a module; known modules: {string.Join(", ", known.Order(StringComparer.Ordinal).Select(name => $"'{name}'"))}.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
