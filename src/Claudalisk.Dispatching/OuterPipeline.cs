namespace Claudalisk.Dispatching;

internal sealed class OuterPipeline(Dispatcher dispatcher) : IOuterPipeline
{
    public Task<TResult> RunAsync<TResult>(object message, MessageKind kind, Func<CancellationToken, Task<TResult>> inner, CancellationToken cancellationToken = default) =>
        dispatcher.RunOuterAsync(message, kind, inner, cancellationToken);
}
