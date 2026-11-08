using System.Reflection;
using Claudalisk.Modules;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class DispatchRegistry
{
    private readonly Dictionary<Type, HandlerRegistration> _handlers;
    private readonly Dictionary<Type, Type[]> _unregisteredValidators;

    public DispatchRegistry(IReadOnlyList<ServiceDescriptor> descriptors)
    {
        var problems = new List<string>();
        var registrations = descriptors.Select(HandlerRegistration.From).OfType<HandlerRegistration>().ToList();
        var catalogs = descriptors.Select(descriptor => descriptor.ImplementationInstance).OfType<ModuleCatalog>().ToArray();
        var modules = catalogs.SelectMany(catalog => catalog.Modules).ToArray();
        var scanned = descriptors.Select(descriptor => descriptor.ImplementationInstance).OfType<DispatchingState>().SelectMany(state => state.Scanned).ToHashSet();

        var registered = descriptors.Select(descriptor => (descriptor.ServiceType, descriptor.ImplementationType)).ToHashSet();
        var validators = new Dictionary<Type, List<Type>>();
        foreach (var assembly in modules.Select(module => module.Assembly).Distinct().Where(assembly => !scanned.Contains(assembly)))
        {
            if (!AssemblyTypes.TryLoad(assembly, exportedOnly: false, out var types, out var problem))
            {
                problems.Add(problem);
                continue;
            }

            foreach (var type in types.Where(type => type.IsClass && !type.IsAbstract && !type.IsGenericTypeDefinition))
            {
                foreach (var contract in type.GetInterfaces().Where(contract => contract.IsGenericType))
                {
                    var definition = contract.GetGenericTypeDefinition();
                    if (registered.Contains((contract, type)))
                    {
                        continue;
                    }

                    if (definition == typeof(IHandler<,>))
                    {
                        var arguments = contract.GetGenericArguments();
                        registrations.Add(new HandlerRegistration(arguments[0], arguments[1], contract, type, ServiceLifetime.Scoped, IsUnregistered: true));
                    }
                    else if (definition == typeof(IValidator<>))
                    {
                        var message = contract.GetGenericArguments()[0];
                        if (!validators.TryGetValue(message, out var list))
                        {
                            validators[message] = list = [];
                        }

                        list.Add(type);
                    }
                }
            }
        }

        var contractAssemblies = modules
            .Select(module => module.Contract?.Assembly)
            .OfType<Assembly>()
            .Distinct()
            .ToArray();
        var contractMessages = ContractMessages(contractAssemblies, problems);

        problems.AddRange(Problems(registrations, contractMessages));
        if (problems.Count > 0)
        {
            throw new InvalidOperationException("The dispatcher's handlers are not valid:" + Environment.NewLine + string.Join(Environment.NewLine, problems));
        }

        _handlers = registrations.Where(registration => !registration.IsKeyed).ToDictionary(registration => registration.MessageType);
        _unregisteredValidators = validators.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
    }

    public HandlerRegistration HandlerFor(Type messageType) =>
        _handlers.TryGetValue(messageType, out var registration)
            ? registration
            : throw new InvalidOperationException($"No handler is registered for '{messageType.FullName}'.");

    public IEnumerable<Type> UnregisteredValidatorsFor(Type messageType) =>
        _unregisteredValidators.TryGetValue(messageType, out var types) ? types : [];

    internal static IReadOnlyCollection<Type> ContractMessages(IEnumerable<Assembly> assemblies, List<string> problems)
    {
        var messages = new List<Type>();
        foreach (var assembly in assemblies)
        {
            if (!AssemblyTypes.TryLoad(assembly, exportedOnly: true, out var types, out var problem))
            {
                problems.Add(problem);
                continue;
            }

            messages.AddRange(types.Where(IsMessageCandidate).Where(type => IsNamedAsMessage(type)));
        }

        return messages;
    }

    internal static bool IsNamedAsMessage(Type type) =>
        type.Name.EndsWith("Command", StringComparison.Ordinal) || type.Name.EndsWith("Query", StringComparison.Ordinal);

    private static bool IsMessageCandidate(Type type) =>
        type.IsVisible
        && (type.IsClass || type.IsValueType)
        && !type.IsEnum
        && !type.IsAbstract
        && !type.IsInterface
        && !type.ContainsGenericParameters
        && !typeof(Delegate).IsAssignableFrom(type);

    internal static IEnumerable<string> Problems(IReadOnlyCollection<HandlerRegistration> registrations, IEnumerable<Type> contractMessages)
    {
        foreach (var keyed in registrations.Where(registration => registration.IsKeyed))
        {
            yield return $"- '{keyed.Name}' is registered as a keyed IHandler<{keyed.MessageType.Name}, {keyed.ResultType.Name}>; the dispatcher resolves handlers without a key, so it would never run.";
        }

        var resolvable = registrations.Where(registration => !registration.IsKeyed).ToArray();
        foreach (var singleton in resolvable.Where(registration => registration.Lifetime == ServiceLifetime.Singleton))
        {
            yield return $"- '{singleton.Name}' is a singleton; handlers must be scoped or transient so they use the caller's scope.";
        }

        foreach (var unnamed in resolvable.Where(registration => !IsNamedAsMessage(registration.MessageType)))
        {
            yield return $"- '{unnamed.Name}' handles '{unnamed.MessageType.FullName}', whose name ends in neither Command nor Query, so it can never be sent.";
        }

        foreach (var duplicate in resolvable.GroupBy(registration => registration.MessageType).Where(group => group.Count() > 1))
        {
            yield return $"- '{duplicate.Key.FullName}' has {duplicate.Count()} handlers: {string.Join(", ", duplicate.Select(registration => registration.Name))}.";
        }

        var handled = resolvable.Select(registration => registration.MessageType).ToHashSet();
        foreach (var missing in contractMessages.Where(message => !handled.Contains(message)).OrderBy(message => message.FullName, StringComparer.Ordinal))
        {
            yield return $"- '{missing.FullName}' has no handler.";
        }
    }
}
