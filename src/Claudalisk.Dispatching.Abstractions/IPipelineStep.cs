namespace Claudalisk.Dispatching;

public interface IPipelineStep
{
    Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken);
}
