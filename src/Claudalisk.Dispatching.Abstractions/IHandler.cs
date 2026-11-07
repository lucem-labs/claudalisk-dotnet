namespace Claudalisk.Dispatching;

public interface IHandler<in TMessage, TResult>
    where TMessage : notnull
{
    Task<TResult> HandleAsync(TMessage message, CancellationToken cancellationToken);
}
