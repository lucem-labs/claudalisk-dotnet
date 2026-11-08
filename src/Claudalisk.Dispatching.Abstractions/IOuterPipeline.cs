namespace Claudalisk.Dispatching;

public interface IOuterPipeline
{
    Task<TResult> RunAsync<TResult>(object message, MessageKind kind, Func<CancellationToken, Task<TResult>> inner, CancellationToken cancellationToken = default);
}
