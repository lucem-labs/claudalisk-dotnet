using Claudalisk.Domain;
using Claudalisk.Modules;

namespace Claudalisk.AspNetCore;

public static class ProblemTypes
{
    public static string For(DomainException exception, ModuleCatalog? catalog)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var module = ModuleNames.ModuleOf(exception.GetType(), catalog);
        return module is null ? $"/problems/{exception.Slug}" : $"/problems/{ModuleNames.Kebab(module)}/{exception.Slug}";
    }
}
