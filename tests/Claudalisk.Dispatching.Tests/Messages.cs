namespace Claudalisk.Dispatching.Tests;

public sealed class OrdersContract;

public sealed class BillingContract;

public sealed class CheckoutContract;

public sealed record PlaceOrderCommand(Guid Id, string Reference);

public sealed record CountOrdersQuery;

public sealed record PeekOrdersQuery(Guid PlaceFirst);

public sealed record WhichScopeQuery;

public sealed record ChargeOrderCommand(Guid OrderId, decimal Amount);

public sealed record CountChargesQuery;

public sealed record CheckOutCommand(Guid OrderId, string Reference, decimal Amount, bool FailAfterBoth);

public sealed record FlakyCommand(Guid Id, int FailAttempts);

public sealed record RelayCommand(Guid Id);

public sealed record ConflictingQuery;

public sealed record GateQuery;

public sealed record FanOutQuery;

public readonly record struct StructCommand(Guid Id);

public static class NestedMessages
{
    public sealed record NestedQuery;
}

public enum ReportKindQuery
{
    Daily,
}

public delegate void RunCommand();

public interface IAuditQuery;

public abstract record BaseCommand(string Name);

public sealed record GenericCommand<T>(T Value);

public interface INamedMessage
{
    string Name { get; }
}

public sealed record DerivedCommand(string Name) : BaseCommand(Name), INamedMessage;

public sealed record InvoiceIssuedEvent(string Number);

public sealed record AbandonQuery;

public sealed record BackgroundQuery;
