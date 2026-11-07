using System.Collections.ObjectModel;

namespace Claudalisk.Dispatching;

public sealed class MessageValidationException : Exception
{
    public MessageValidationException(Type messageType, IReadOnlyDictionary<string, string[]> errors)
        : base($"'{messageType?.Name}' is not valid: {string.Join("; ", errors?.SelectMany(error => error.Value.Select(message => $"{error.Key}: {message}")) ?? [])}")
    {
        ArgumentNullException.ThrowIfNull(messageType);
        ArgumentNullException.ThrowIfNull(errors);
        MessageType = messageType;
        Errors = new ReadOnlyDictionary<string, string[]>(errors.ToDictionary(error => error.Key, error => error.Value.ToArray(), StringComparer.Ordinal));
    }

    public Type MessageType { get; }

    public IReadOnlyDictionary<string, string[]> Errors { get; }
}
