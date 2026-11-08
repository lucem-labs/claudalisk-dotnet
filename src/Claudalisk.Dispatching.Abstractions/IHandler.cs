namespace Claudalisk.Dispatching;

public interface IHandler<in TMessage, TResult>
    where TMessage : notnull
{
    ValueTask<TResult> HandleAsync(TMessage message, CancellationToken cancellationToken);
}
