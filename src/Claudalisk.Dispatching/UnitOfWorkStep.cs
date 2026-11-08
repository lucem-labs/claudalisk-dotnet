using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class UnitOfWorkStep(IServiceProvider scope) : IPipelineStep
{
    public int Order => PipelineOrder.UnitOfWork;

    public Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        if (context.Kind != MessageKind.Command)
        {
            return next(cancellationToken);
        }

        var unitOfWork = scope.GetService<IUnitOfWork>()
            ?? throw new InvalidOperationException(
                $"'{context.MessageType.FullName}' is a command, and commands run in a unit of work, but no IUnitOfWork is registered. Call AddClaudaliskPersistence.");
        return unitOfWork.RunAsync(next, cancellationToken);
    }
}
