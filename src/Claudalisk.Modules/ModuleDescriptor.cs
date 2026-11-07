using System.Reflection;

namespace Claudalisk.Modules;

public sealed record ModuleDescriptor(string Name, Type ModuleType, Assembly Assembly, Type? Contract, string? Description)
{
    internal static ModuleDescriptor For(IModule module)
    {
        var type = module.GetType();
        var name = type.Name.EndsWith("Module", StringComparison.Ordinal) && type.Name.Length > "Module".Length
            ? type.Name[..^"Module".Length]
            : type.Name;
        return new ModuleDescriptor(name, type, type.Assembly, module.Contract, module.Description);
    }
}
