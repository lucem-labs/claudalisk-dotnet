using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Claudalisk.Dispatching;

internal sealed class Dispatcher(IServiceProvider scope, DispatchRegistry registry, IEnumerable<IPipelineStep> steps) : IDispatcher
{
    private static readonly AsyncLocal<Dispatcher?> Sending = new();
    private static readonly ConcurrentDictionary<(Type Message, Type Result), Func<object, object, CancellationToken, Task<object?>>> Invokers = new();
    private static readonly MethodInfo InvokeMethod = typeof(Dispatcher).GetMethod(nameof(InvokeAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IPipelineStep[] _steps = steps.ToArray();
    private int _active;

    public async Task<TResult> SendAsync<TResult>(object message, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        MessageContext.KindOf(message.GetType());
        var handler = registry.HandlerFor(message.GetType());
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
        var handler = registry.HandlerFor(messageType);

        if (Volatile.Read(ref _active) > 0 && Sending.Value != this)
        {
            throw new InvalidOperationException(
                "A message was sent while another send in the same scope was still running. Sends in one scope run one at a time; await each send before the next.");
        }

        Interlocked.Increment(ref _active);
        Sending.Value = this;
        try
        {
            var context = new MessageContext(message, messageType, handler.ResultType, kind);
            var invoke = Invokers.GetOrAdd((messageType, handler.ResultType), key =>
                InvokeMethod.MakeGenericMethod(key.Message, key.Result).CreateDelegate<Func<object, object, CancellationToken, Task<object?>>>());
            return await Run(context, 0, token => invoke(scope.GetRequiredService(handler.ServiceType), message, token), cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private Task<object?> Run(MessageContext context, int index, Func<CancellationToken, Task<object?>> handle, CancellationToken cancellationToken) =>
        index == _steps.Length
            ? handle(cancellationToken)
            : _steps[index].InvokeAsync(context, token => Run(context, index + 1, handle, token), cancellationToken);

    private static async Task<object?> InvokeAsync<TMessage, TResult>(object handler, object message, CancellationToken cancellationToken)
        where TMessage : notnull =>
        await ((IHandler<TMessage, TResult>)handler).HandleAsync((TMessage)message, cancellationToken);
}
