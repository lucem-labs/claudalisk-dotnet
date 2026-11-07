namespace Claudalisk.Modules;

internal sealed record ModuleDeclaration(IModule Module, Type ModuleType, Type Contract, IReadOnlyCollection<Type> Dependencies)
{
    public static ModuleDeclaration Read(IModule? module, string parameterName)
    {
        if (module is null)
        {
            throw new ArgumentException("The module list contains null.", parameterName);
        }

        var moduleType = module.GetType();
        var contract = module.Contract ?? throw new ArgumentException($"{moduleType.Name} has no contract.", parameterName);
        if (!contract.IsClass || contract.ContainsGenericParameters || contract == moduleType)
        {
            throw new ArgumentException($"{moduleType.Name} declares {contract.Name} as its contract, but a contract is the module's marker class from its Interfaces project.", parameterName);
        }

        var dependencies = module.Dependencies ?? throw new ArgumentException($"{moduleType.Name} has no dependency list.", parameterName);
        if (dependencies.Any(dependency => dependency is null))
        {
            throw new ArgumentException($"{moduleType.Name} lists null as a dependency.", parameterName);
        }

        return new ModuleDeclaration(module, moduleType, contract, dependencies.Distinct().ToList().AsReadOnly());
    }
}
