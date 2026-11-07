namespace Claudalisk.Dispatching;

public sealed record MessageContext(object Message, Type MessageType, Type ResultType, MessageKind Kind)
{
    public static MessageKind KindOf(Type messageType)
    {
        ArgumentNullException.ThrowIfNull(messageType);
        if (messageType.Name.EndsWith("Command", StringComparison.Ordinal))
        {
            return MessageKind.Command;
        }

        if (messageType.Name.EndsWith("Query", StringComparison.Ordinal))
        {
            return MessageKind.Query;
        }

        throw new InvalidOperationException(
            $"'{messageType.FullName}' cannot be sent: a message's name ends in Command or Query.");
    }
}
