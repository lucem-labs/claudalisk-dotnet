namespace Claudalisk.Dispatching;

public interface IDispatcher
{
    ValueTask<TResult> SendAsync<TResult>(object message, CancellationToken cancellationToken = default);

    ValueTask<object?> SendAsync(object message, CancellationToken cancellationToken = default);
}
