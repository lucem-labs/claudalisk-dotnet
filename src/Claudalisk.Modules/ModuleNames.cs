using System.Text;

namespace Claudalisk.Modules;

internal static class ModuleNames
{
    public static string? ModuleOf(Type type, ModuleCatalog? catalog)
    {
        var assembly = type.Assembly.GetName().Name ?? "";
        var owner = catalog?.Modules
            .Where(module => assembly == module.Assembly.GetName().Name || assembly.StartsWith(module.Assembly.GetName().Name + ".", StringComparison.Ordinal))
            .OrderByDescending(module => module.Assembly.GetName().Name!.Length)
            .FirstOrDefault();
        if (owner is not null)
        {
            return owner.Name;
        }

        var parts = (type.Namespace ?? "").Split('.');
        var index = Array.IndexOf(parts, "Modules");
        return index >= 0 && index + 1 < parts.Length ? parts[index + 1] : null;
    }

    public static string Kebab(string name)
    {
        var builder = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
            {
                builder.Append('-');
            }

            builder.Append(char.ToLowerInvariant(name[i]));
        }

        return builder.ToString();
    }
}
