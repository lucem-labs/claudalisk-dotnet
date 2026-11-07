namespace Claudalisk.Modules;

public sealed class ModuleRegistry
{
    private readonly List<RegisteredModule> _modules = [];

    internal ModuleRegistry()
    {
    }

    public IReadOnlyList<RegisteredModule> Modules => _modules.AsReadOnly();

    internal bool ControllersAdded { get; set; }

    internal void Add(RegisteredModule module) => _modules.Add(module);
}
