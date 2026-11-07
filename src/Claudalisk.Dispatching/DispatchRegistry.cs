using Claudalisk.Modules;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class DispatchRegistry
{
    private readonly Dictionary<Type, HandlerRegistration> _handlers;

    public DispatchRegistry(IServiceCollection services)
    {
        var registrations = services.Select(HandlerRegistration.From).OfType<HandlerRegistration>().ToArray();
        var contractAssemblies = services
            .Select(descriptor => descriptor.ImplementationInstance)
            .OfType<ModuleCatalog>()
            .SelectMany(catalog => catalog.Modules)
            .Select(module => module.Contract?.Assembly)
            .OfType<System.Reflection.Assembly>()
            .Distinct()
            .ToArray();

        var problems = Problems(registrations, ContractMessages(contractAssemblies)).ToArray();
        if (problems.Length > 0)
        {
            throw new InvalidOperationException("The dispatcher's handlers are not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        _handlers = registrations.ToDictionary(registration => registration.MessageType);
    }

    public HandlerRegistration HandlerFor(Type messageType) =>
        _handlers.TryGetValue(messageType, out var registration)
            ? registration
            : throw new InvalidOperationException($"No handler is registered for '{messageType.FullName}'.");

    internal static IEnumerable<Type> ContractMessages(IEnumerable<System.Reflection.Assembly> assemblies) =>
        assemblies
            .SelectMany(assembly => assembly.GetExportedTypes())
            .Where(type => type.IsPublic && type.IsClass && !type.IsAbstract)
            .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal) || type.Name.EndsWith("Query", StringComparison.Ordinal));

    internal static IEnumerable<string> Problems(IReadOnlyCollection<HandlerRegistration> registrations, IEnumerable<Type> contractMessages)
    {
        foreach (var singleton in registrations.Where(registration => registration.Lifetime == ServiceLifetime.Singleton))
        {
            yield return $"- '{singleton.Name}' is a singleton; handlers must be scoped or transient so they use the caller's scope.";
        }

        foreach (var duplicate in registrations.GroupBy(registration => registration.MessageType).Where(group => group.Count() > 1))
        {
            yield return $"- '{duplicate.Key.FullName}' has {duplicate.Count()} handlers: {string.Join(", ", duplicate.Select(registration => registration.Name))}.";
        }

        var handled = registrations.Select(registration => registration.MessageType).ToHashSet();
        foreach (var missing in contractMessages.Where(message => !handled.Contains(message)).OrderBy(message => message.FullName, StringComparer.Ordinal))
        {
            yield return $"- '{missing.FullName}' has no handler.";
        }
    }
}
