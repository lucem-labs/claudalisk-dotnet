namespace Claudalisk.Modules;

public interface IModule
{
    Type Contract { get; }

    IReadOnlyCollection<Type> Dependencies { get; }

    void Register(ModuleRegistration registration);
}
