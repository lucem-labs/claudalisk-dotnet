using System.Reflection;

namespace Claudalisk.Dispatching;

internal static class AssemblyTypes
{
    public static bool TryLoad(Assembly assembly, bool exportedOnly, out Type[] types, out string problem)
    {
        try
        {
            types = exportedOnly ? assembly.GetExportedTypes() : assembly.GetTypes();
            problem = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ReflectionTypeLoadException or IOException or BadImageFormatException or TypeLoadException or NotSupportedException)
        {
            var reasons = exception is ReflectionTypeLoadException load
                ? load.LoaderExceptions.OfType<Exception>().Select(inner => inner.Message).Distinct(StringComparer.Ordinal).Take(3)
                : [exception.Message];
            types = [];
            problem = $"- The types of assembly '{assembly.GetName().Name}' could not be loaded ({exception.GetType().Name}): {string.Join(" | ", reasons)}";
            return false;
        }
    }
}
