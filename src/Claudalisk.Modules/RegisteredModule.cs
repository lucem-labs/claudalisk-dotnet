namespace Claudalisk.Modules;

public sealed class RegisteredModule
{
    internal RegisteredModule(Type moduleType, Type contract, IReadOnlyCollection<Type> dependencies)
    {
        ModuleType = moduleType;
        Contract = contract;
        Dependencies = dependencies;
    }

    public Type ModuleType { get; }

    public Type Contract { get; }

    public IReadOnlyCollection<Type> Dependencies { get; }
}
