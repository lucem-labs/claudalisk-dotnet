using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Modules;

public sealed class ModuleCatalog
{
    private readonly List<ModuleDescriptor> _modules = [];

    internal ModuleCatalog(IMvcBuilder mvc)
    {
        Mvc = mvc;
    }

    public IReadOnlyList<ModuleDescriptor> Modules => _modules;

    internal IMvcBuilder Mvc { get; }

    internal Dictionary<Type, IModule> Registered { get; } = [];

    internal void Add(IModule module)
    {
        Registered.Add(module.GetType(), module);
        _modules.Add(ModuleDescriptor.For(module));
    }
}
