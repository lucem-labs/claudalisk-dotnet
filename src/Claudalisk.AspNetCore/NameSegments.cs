namespace Claudalisk.AspNetCore;

public static class NameSegments
{
    public static string Kebab(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return ModuleNames.Kebab(name);
    }
}
