namespace Claudalisk.Dispatching;

public interface IPipelineStep
{
    int Order { get; }

    Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken);
}
