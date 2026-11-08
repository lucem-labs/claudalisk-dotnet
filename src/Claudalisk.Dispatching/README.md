# Claudalisk.Dispatching

The in-process dispatcher. `AddDispatching` finds the `IHandler<,>` and `IValidator<>` classes of every module assembly and registers them as scoped services. `IDispatcher.SendAsync` runs a message through the pipeline and into its handler. A message's name ends in `Command` or `Query`.

## Pipeline order

Every `IPipelineStep` states an `Order`. The dispatcher sorts steps by it, and steps with equal orders keep their registration order. When the step was registered no longer matters.

| Order | Step |
| --- | --- |
| `PipelineOrder.Logging` (1000) | logging |
| `PipelineOrder.Validation` (2000) | validation |
| `PipelineOrder.Retry` (3000) | the dispatcher's retry; no step may use it |
| `PipelineOrder.UnitOfWork` (4000) | the unit of work (commands only) |
| 5000 | the jobs library |

Steps below `Retry` are outer steps and run once, in the caller's scope. Steps above it are inner steps and run inside each attempt. The startup check rejects a step whose order equals `PipelineOrder.Retry`.

## Retry

A command that loses a concurrency race is sent again. The retry applies only when all of these hold:

- the message is a command
- no other send is running (it is not a nested send)
- no unit of work is active in the caller's scope (`IUnitOfWork.IsActive` is false)

Outer steps run once. Then each attempt, the first included, gets a fresh scope from `IServiceScopeFactory`, runs the inner steps and resolves the handler there. The attempt scope is disposed afterwards; a failure while disposing it is logged at Warning and does not change the attempt's outcome.

Because every attempt runs in a fresh scope, scoped state the caller prepared is not visible to the handler: values that middleware put into a scoped holder (the current user or tenant) or anything set on a scoped service before sending. Resolve such state from a singleton or an accessor such as `IHttpContextAccessor`, or pass it in the message. Sends made by the handler go through the attempt scope's dispatcher as nested sends and are not retried on their own.

An attempt is repeated only when it throws `ConcurrencyConflictException` itself (not wrapped) and the token is not canceled. There is no delay. `DispatchingOptions.ConcurrencyAttempts` (default 3, at least 1) is the total number of attempts; after the last one the exception propagates. Each retry is logged at Information.

A command sent while a unit of work is active, for example inside `IUnitOfWork.RunAsync`, joins it: the caller's scope, no retry. Queries are never retried.

## One send at a time

Sends in one scope run one at a time. A send that starts while another send of the same scope is running, whether top-level or from inside a handler (`Task.WhenAll` of two sends), throws `InvalidOperationException`. Await each send before the next. The check is atomic.

## Outer steps without a handler

`IOuterPipeline.RunAsync(message, kind, inner)` runs the outer steps for a message, then `inner`, in the current scope. There is no retry, unit of work or inner step. It counts as a send, so sends from inside `inner` are nested. The kind is given explicitly and may be `MessageKind.Event`.

## Validators

Validation is by exact type: only the `IValidator<T>` registered for the message's own type runs. A validator for a base type or an interface does not run. Failures are collected into one `MessageValidationException` with camelCase keys.

## Startup check

`ValidateDispatching` and the hosted startup check build the registry and throw one `InvalidOperationException` listing every problem:

- a singleton handler
- a message with several handlers
- a handler for a message whose name ends in neither `Command` nor `Query`
- a keyed `IHandler<,>` registration; the dispatcher resolves handlers without a key
- a public class, struct or nested type of a module contract assembly named `...Command` or `...Query` without a handler
- an assembly whose types cannot be loaded, with the first loader messages

A pipeline step with `Order` equal to `PipelineOrder.Retry` is rejected when the hosted startup check or `ValidateDispatching` runs, and again when a scope's dispatcher is created. The registry itself never constructs steps.

The registry reads the service collection once, when the provider first builds it, and keeps no reference to the collection afterwards. A handler or validator in a module assembly that was added to the catalog after `AddDispatching` is found at that moment and created per send with `ActivatorUtilities`, so it is not disposed with the scope.

## Logging

Each send logs at Debug. A canceled send logs at Debug, a validation failure at Information without the exception, and any other failure at Information with it.
