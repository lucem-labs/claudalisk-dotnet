# Claudalisk.Dispatching.Abstractions

The contracts of the dispatcher, and the only dispatching package that an Interfaces project references. `Claudalisk.Dispatching` implements them.

## Messages and handlers

A message is a class whose name ends in `Command` or `Query`. `MessageContext.KindOf` throws `InvalidOperationException` for any other name.

```csharp
public sealed class ShipOrderHandler : IHandler<ShipOrderCommand, ShippedOrder>
{
    public ValueTask<ShippedOrder> HandleAsync(ShipOrderCommand message, CancellationToken cancellationToken) { ... }
}
```

`IDispatcher.SendAsync<TResult>(message)` finds the handler for the message type and returns its result. The overload without a type argument returns `object?`.

## Pipeline

`IPipelineStep` has an `Order` and an `InvokeAsync(context, next, cancellationToken)`. The dispatcher sorts steps by order. `PipelineOrder` names the built-in positions: `Logging` 1000, `Validation` 2000, `Retry` 3000 and `UnitOfWork` 4000. Steps below `Retry` are outer steps and run once. Steps above it run in each attempt. No step may use the `Retry` order.

`IOuterPipeline.RunAsync` runs only the outer steps around a delegate, for work that has no handler. Its kind can also be `MessageKind.Event`.

## Other types

- `MessageContext`: the message, its type, its result type and its `MessageKind`.
- `MessageValidationException`: carries `MessageType` and `Errors`, a map from member name to messages. The web package maps it to 400.
- `JobReference`: module, job id and URL of a queued job.
