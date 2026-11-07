namespace Claudalisk.Dispatching;

public interface IDispatcher
{
    Task<TResult> SendAsync<TResult>(object message, CancellationToken cancellationToken = default);

    Task<object?> SendAsync(object message, CancellationToken cancellationToken = default);
}
