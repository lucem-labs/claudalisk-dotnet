using System.Collections.Concurrent;
using System.Reflection;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Claudalisk.Dispatching;

internal sealed partial class Dispatcher : IDispatcher
{
    private static readonly AsyncLocal<SendFrame?> Ambient = new();
    private static readonly ConcurrentDictionary<(Type Message, Type Result), Func<object, object, CancellationToken, Task<object?>>> Invokers = new();
    private static readonly MethodInfo InvokeMethod = typeof(Dispatcher).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IServiceProvider _scope;
    private readonly DispatchRegistry _registry;
    private readonly IPipelineStep[] _steps;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly DispatchingOptions _options;
    private readonly ILogger _logger;
    private SendFrame? _current;

    public Dispatcher(IServiceProvider scope, DispatchRegistry registry, IEnumerable<IPipelineStep> steps, IServiceScopeFactory scopeFactory, IOptions<DispatchingOptions> options)
    {
        _scope = scope;
        _registry = registry;
        _steps = [.. steps.OrderBy(step => step.Order)];
        PipelineSteps.RejectRetrySlot(_steps);
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _logger = scope.GetService<ILogger<IDispatcher>>() ?? NullLogger<IDispatcher>.Instance;
    }

    public async Task<TResult> SendAsync<TResult>(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessageContext.KindOf(message.GetType());
        var handler = _registry.HandlerFor(message.GetType());
        if (handler.ResultType != typeof(TResult))
        {
            throw new InvalidOperationException(
                $"'{message.GetType().FullName}' was sent for a '{typeof(TResult).FullName}', but its handler returns '{handler.ResultType.FullName}'.");
        }

        return (TResult)(await SendAsync(message, cancellationToken))!;
    }

    public async Task<object?> SendAsync(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var messageType = message.GetType();
        var kind = MessageContext.KindOf(messageType);
        var handler = _registry.HandlerFor(messageType);

        var frame = Enter();
        try
        {
            var context = new MessageContext(message, messageType, handler.ResultType, kind);
            var invoke = Invokers.GetOrAdd((messageType, handler.ResultType), key =>
                InvokeMethod.MakeGenericMethod(key.Message, key.Result).CreateDelegate<Func<object, object, CancellationToken, Task<object?>>>());
            if (kind == MessageKind.Command && frame.IsTopLevel && _scope.GetService<IUnitOfWork>() is not { IsActive: true })
            {
                var outer = _steps.Where(step => step.Order < PipelineOrder.Retry);
                return await Run(context, outer, token => AttemptsAsync(context, handler, invoke, token), cancellationToken);
            }

            return await Run(context, _steps, token => invoke(handler.Resolve(_scope), message, token), cancellationToken);
        }
        finally
        {
            Exit(frame);
        }
    }

    internal async Task<TResult> RunOuterAsync<TResult>(object message, MessageKind kind, Func<CancellationToken, Task<TResult>> inner, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(inner);
        var frame = Enter();
        try
        {
            var context = new MessageContext(message, message.GetType(), typeof(TResult), kind);
            var outer = _steps.Where(step => step.Order < PipelineOrder.Retry);
            var result = await Run(context, outer, async token => await inner(token), cancellationToken);
            return (TResult)result!;
        }
        finally
        {
            Exit(frame);
        }
    }

    private async Task<object?> AttemptsAsync(
        MessageContext context,
        HandlerRegistration handler,
        Func<object, object, CancellationToken, Task<object?>> invoke,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var attemptScope = _scopeFactory.CreateAsyncScope();
            try
            {
                var inner = attemptScope.ServiceProvider.GetServices<IPipelineStep>()
                    .Where(step => step.Order > PipelineOrder.Retry)
                    .OrderBy(step => step.Order);
                return await Run(context, inner, token => invoke(handler.Resolve(attemptScope.ServiceProvider), context.Message, token), cancellationToken);
            }
            catch (ConcurrencyConflictException) when (attempt < _options.ConcurrencyAttempts && !cancellationToken.IsCancellationRequested)
            {
                LogRetrying(_logger, context.MessageType.Name, attempt, _options.ConcurrencyAttempts);
            }
            finally
            {
                await DisposeQuietlyAsync(attemptScope, context.MessageType.Name);
            }
        }
    }

    private async Task DisposeQuietlyAsync(AsyncServiceScope attemptScope, string message)
    {
        try
        {
            await attemptScope.DisposeAsync();
        }
        catch (Exception exception)
        {
            LogScopeDisposeFailed(_logger, exception, message);
        }
    }

    private SendFrame Enter()
    {
        var ambient = Ambient.Value;
        var expected = NearestLive(ambient, this);
        var frame = new SendFrame(this, expected, ambient, IsLive(ambient));
        if (!ReferenceEquals(Interlocked.CompareExchange(ref _current, frame, expected), expected))
        {
            throw new InvalidOperationException(
                "A message was sent while another send in the same scope was still running. Sends in one scope run one at a time; await each send before the next.");
        }

        Ambient.Value = frame;
        return frame;
    }

    private void Exit(SendFrame frame)
    {
        frame.MarkExited();
        while (Volatile.Read(ref _current) is { Exited: true } dead)
        {
            var target = dead.Parent;
            while (target is { Exited: true })
            {
                target = target.Parent;
            }

            Interlocked.CompareExchange(ref _current, target, dead);
        }

        Ambient.Value = frame.Previous;
    }

    private static SendFrame? NearestLive(SendFrame? ambient, Dispatcher owner)
    {
        for (var frame = ambient; frame is not null; frame = frame.Previous)
        {
            if (!frame.Exited && ReferenceEquals(frame.Owner, owner))
            {
                return frame;
            }
        }

        return null;
    }

    private static bool IsLive(SendFrame? ambient)
    {
        for (var frame = ambient; frame is not null; frame = frame.Previous)
        {
            if (!frame.Exited)
            {
                return true;
            }
        }

        return false;
    }

    private static Task<object?> Run(MessageContext context, IEnumerable<IPipelineStep> steps, Func<CancellationToken, Task<object?>> handle, CancellationToken cancellationToken) =>
        Run(context, steps.ToArray(), 0, handle, cancellationToken);

    private static Task<object?> Run(MessageContext context, IPipelineStep[] steps, int index, Func<CancellationToken, Task<object?>> handle, CancellationToken cancellationToken) =>
        index == steps.Length
            ? handle(cancellationToken)
            : steps[index].InvokeAsync(context, token => Run(context, steps, index + 1, handle, token), cancellationToken);

    private static async Task<object?> InvokeAsync<TMessage, TResult>(object handler, object message, CancellationToken cancellationToken)
        where TMessage : notnull =>
        await ((IHandler<TMessage, TResult>)handler).HandleAsync((TMessage)message, cancellationToken);

    [LoggerMessage(Level = LogLevel.Information, Message = "Command {Message} lost a concurrency race on attempt {Attempt} of {Attempts}; trying again")]
    private static partial void LogRetrying(ILogger logger, string message, int attempt, int attempts);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Disposing the attempt scope of command {Message} failed; the attempt's outcome is unchanged")]
    private static partial void LogScopeDisposeFailed(ILogger logger, Exception exception, string message);

    private sealed class SendFrame(Dispatcher owner, SendFrame? parent, SendFrame? previous, bool hasLiveAmbient)
    {
        private int _exited;

        public Dispatcher Owner { get; } = owner;

        public SendFrame? Parent { get; } = parent;

        public SendFrame? Previous { get; } = previous;

        public bool IsTopLevel { get; } = !hasLiveAmbient;

        public bool Exited => Volatile.Read(ref _exited) == 1;

        public void MarkExited() => Interlocked.Exchange(ref _exited, 1);
    }
}
