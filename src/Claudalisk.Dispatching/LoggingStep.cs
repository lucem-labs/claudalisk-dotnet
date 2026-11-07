using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Claudalisk.Dispatching;

internal sealed partial class LoggingStep(IServiceProvider scope) : IPipelineStep
{
    private readonly ILogger _logger = scope.GetService<ILogger<IDispatcher>>() ?? NullLogger<IDispatcher>.Instance;

    public async Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        LogHandling(_logger, context.Kind, context.MessageType.Name);
        try
        {
            var result = await next(cancellationToken);
            LogHandled(_logger, context.Kind, context.MessageType.Name);
            return result;
        }
        catch (Exception exception)
        {
            LogFailed(_logger, exception, context.Kind, context.MessageType.Name, exception.Message);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handling {Kind} {Message}")]
    private static partial void LogHandling(ILogger logger, MessageKind kind, string message);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Handled {Kind} {Message}")]
    private static partial void LogHandled(ILogger logger, MessageKind kind, string message);

    [LoggerMessage(Level = LogLevel.Information, Message = "{Kind} {Message} failed: {Error}")]
    private static partial void LogFailed(ILogger logger, Exception exception, MessageKind kind, string message, string error);
}
