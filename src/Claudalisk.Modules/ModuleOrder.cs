namespace Claudalisk.Modules;

internal static class ModuleOrder
{
    public static IReadOnlyList<IModule> Resolve(IEnumerable<IModule> available, IReadOnlySet<Type> alreadyRegistered)
    {
        var modules = available.ToArray();
        foreach (var module in modules)
        {
            RequireMarker(module.GetType(), module.Contract, "contract");
            foreach (var dependency in module.Dependencies)
            {
                RequireMarker(module.GetType(), dependency, "dependency");
            }
        }

        var ordered = new List<IModule>();
        var visited = new HashSet<Type>();
        var visiting = new List<Type>();

        void Visit(IModule module)
        {
            var moduleType = module.GetType();
            if (visited.Contains(moduleType))
            {
                return;
            }

            if (visiting.Contains(moduleType))
            {
                throw new InvalidOperationException(
                    $"Circular module dependency: {string.Join(" -> ", visiting.Append(moduleType).Select(type => type.FullName))}.");
            }

            visiting.Add(moduleType);
            foreach (var dependency in module.Dependencies)
            {
                var providers = modules.Where(candidate => candidate.Contract == dependency).ToArray();
                if (providers.Length == 0)
                {
                    throw new InvalidOperationException(
                        $"Module '{moduleType.FullName}' requires '{dependency.FullName}', but no module passed to AddModules provides it.");
                }

                if (providers.Length > 1)
                {
                    throw new InvalidOperationException(
                        $"Module '{moduleType.FullName}' has an ambiguous dependency on '{dependency.FullName}': "
                        + string.Join(", ", providers.Select(provider => provider.GetType().FullName)) + " all provide it.");
                }

                Visit(providers[0]);
            }

            visiting.RemoveAt(visiting.Count - 1);
            visited.Add(moduleType);
            if (!alreadyRegistered.Contains(moduleType))
            {
                ordered.Add(module);
            }
        }

        foreach (var module in modules)
        {
            Visit(module);
        }

        return ordered;
    }

    private static void RequireMarker(Type module, Type? marker, string role)
    {
        if (marker is not null && !(marker.IsClass && marker.IsSealed && !marker.IsAbstract))
        {
            throw new InvalidOperationException(
                $"Module '{module.FullName}' names '{marker.FullName}' as a {role}, but a module marker must be an empty sealed class from an Interfaces project.");
        }
    }
}
