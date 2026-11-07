namespace Claudalisk.Modules;

internal static class ModuleOrder
{
    public static IReadOnlyList<ModuleDeclaration> Resolve(IReadOnlyList<RegisteredModule> registered, IReadOnlyList<ModuleDeclaration> candidates)
    {
        var providers = registered.Select(module => (module.Contract, module.ModuleType))
            .Concat(candidates.Select(module => (module.Contract, module.ModuleType)))
            .GroupBy(provider => provider.Contract)
            .ToList();

        var errors = providers
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key.Name} is ambiguous: it is the contract of {string.Join(", ", group.Select(provider => provider.ModuleType.Name))}.")
            .Concat(candidates.SelectMany(module => module.Dependencies
                .Where(dependency => !providers.Any(group => group.Key == dependency))
                .Select(dependency => $"{module.ModuleType.Name} depends on {dependency.Name}, but no module declares it as its contract.")))
            .ToList();

        if (errors.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, ["The modules cannot be registered.", .. errors]));
        }

        return Sort(candidates);
    }

    private static List<ModuleDeclaration> Sort(IReadOnlyList<ModuleDeclaration> candidates)
    {
        var byContract = candidates.ToDictionary(module => module.Contract);
        var ordered = new List<ModuleDeclaration>(candidates.Count);
        var done = new HashSet<ModuleDeclaration>();
        var path = new List<ModuleDeclaration>();

        void Visit(ModuleDeclaration module)
        {
            if (done.Contains(module))
            {
                return;
            }

            var start = path.IndexOf(module);
            if (start >= 0)
            {
                var cycle = path.Skip(start).Append(module).Select(step => step.ModuleType.Name);
                throw new InvalidOperationException($"The modules cannot be registered.{Environment.NewLine}They have a circular dependency: {string.Join(" -> ", cycle)}.");
            }

            path.Add(module);
            foreach (var dependency in module.Dependencies)
            {
                if (byContract.TryGetValue(dependency, out var provider))
                {
                    Visit(provider);
                }
            }

            path.RemoveAt(path.Count - 1);
            done.Add(module);
            ordered.Add(module);
        }

        foreach (var module in candidates)
        {
            Visit(module);
        }

        return ordered;
    }
}
