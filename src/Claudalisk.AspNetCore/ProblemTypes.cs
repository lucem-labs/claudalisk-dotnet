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

    internal static string? Resolve(string? type, Uri? typeBase) =>
        type is null || typeBase is null ? type : $"{typeBase.AbsoluteUri.TrimEnd('/')}{type}";

    internal static bool IsValidBase(Uri? typeBase) =>
        typeBase is null
        || (typeBase.IsAbsoluteUri && (typeBase.Scheme == Uri.UriSchemeHttps || typeBase.Scheme == Uri.UriSchemeHttp) && typeBase.Query.Length == 0 && typeBase.Fragment.Length == 0);
}
