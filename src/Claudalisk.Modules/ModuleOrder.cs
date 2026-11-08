namespace Claudalisk.Modules;

internal static class ModuleOrder
{
    public static IReadOnlyList<IModule> Resolve(IEnumerable<IModule> available, IReadOnlySet<Type> alreadyRegistered)
    {
        var modules = available.ToArray();
        foreach (var module in modules)
        {
            RequireMarker(module.GetType(), module.Contract, "contract");
            var dependencies = module.Dependencies
                ?? throw new InvalidOperationException($"Module '{module.GetType().FullName}' has a null Dependencies collection.");
            foreach (var dependency in dependencies)
            {
                if (dependency is null)
                {
                    throw new InvalidOperationException($"Module '{module.GetType().FullName}' has a null entry in its Dependencies.");
                }

                RequireMarker(module.GetType(), dependency, "dependency");
            }
        }

        var contractOwners = new Dictionary<Type, Type>();
        foreach (var module in modules)
        {
            if (module.Contract is { } contract && !contractOwners.TryAdd(contract, module.GetType()))
            {
                throw new InvalidOperationException(
                    $"Modules '{contractOwners[contract].FullName}' and '{module.GetType().FullName}' both provide the contract '{contract.FullName}'.");
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
                $"Module '{module.FullName}' names '{marker.FullName}' as a {role}, but a module marker must be a sealed class from an Interfaces project.");
        }
    }
}
