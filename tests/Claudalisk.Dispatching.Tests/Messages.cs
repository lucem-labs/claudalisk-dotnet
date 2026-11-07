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
