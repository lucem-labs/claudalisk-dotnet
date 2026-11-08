using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Claudalisk.Dispatching;
using Claudalisk.Domain;
using Claudalisk.Modules;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs;

public sealed partial class JobRunner
{
    private static readonly MethodInfo BatchMethod = typeof(JobRunner).GetMethod(nameof(RunBatchesAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo RebuildMethod = typeof(JobRunner).GetMethod(nameof(RebuildAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo EventMethod = typeof(JobRunner).GetMethod(nameof(HandleEventAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly ConcurrentDictionary<(Type Payload, Type Checkpoint), Func<IServiceProvider, JobKind, JobContext, object, string?, CancellationToken, Task>> Batches = new();
    private static readonly ConcurrentDictionary<(Type Payload, Type Event), Func<IServiceProvider, JobKind, JobContext, object, CancellationToken, Task>> Rebuilds = new();
    private static readonly ConcurrentDictionary<Type, Func<object, object, CancellationToken, Task>> Events = new();

    private readonly IServiceScopeFactory _scopes;
    private readonly IJobTransport _transport;
    private readonly JobsRegistry _registry;
    private readonly JobsOptions _options;
    private readonly ILogger<JobRunner> _logger;
    private readonly TimeProvider _time;
    private readonly ModuleCatalog? _catalog;

    internal JobRunner(
        IServiceScopeFactory scopes, IJobTransport transport, JobsRegistry registry, IOptions<JobsOptions> options, ILogger<JobRunner> logger, TimeProvider time,
        ModuleCatalog? catalog)
    {
        _scopes = scopes;
        _transport = transport;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
        _time = time;
        _catalog = catalog;
    }

    public async Task<int> RunUntilIdleAsync(string module, string lane = JobOptions.DefaultLane, CancellationToken cancellationToken = default)
    {
        var ran = 0;
        while (await RunNextAsync(module, lane, cancellationToken).ConfigureAwait(false))
        {
            ran++;
        }

        return ran;
    }

    public async Task<bool> RunNextAsync(string module, string lane = JobOptions.DefaultLane, CancellationToken cancellationToken = default)
    {
        var jobs = _registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
        var claimed = await _transport.ClaimAsync(jobs, lane, cancellationToken).ConfigureAwait(false);
        if (claimed is null)
        {
            return false;
        }

        await RunAsync(jobs, claimed, cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task RunAsync(JobsModule module, ClaimedJob claimed, CancellationToken stopping)
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        using var heartbeatStop = new CancellationTokenSource();
        var heartbeat = BeatAsync(module, claimed.Id, cancel, heartbeatStop.Token);
        var cancelWatch = WatchForCancelAsync(module, claimed.Id, cancel, heartbeatStop.Token);
        try
        {
            if (!module.Kinds.TryGetValue(claimed.Kind, out var kind))
            {
                throw new InvalidOperationException($"Module '{module.Name}' has no job kind '{claimed.Kind}'.");
            }

            var payload = JsonSerializer.Deserialize(claimed.Payload, kind.PayloadType, JobQueue.Json)
                ?? throw new InvalidOperationException($"Job {claimed.Id} has an empty payload.");
            var scope = _scopes.CreateAsyncScope();
            await using var scopeDisposal = scope.ConfigureAwait(false);
            var context = scope.ServiceProvider.GetRequiredService<JobContext>();
            context.Start(module, claimed, payload);
            switch (kind.Type)
            {
                case JobKindType.Command:
                    var unitOfWork = scope.ServiceProvider.GetService<IUnitOfWork>()
                        ?? throw new InvalidOperationException(
                            $"Job kind '{kind.Name}' is not a command that runs in a unit of work; register AddJobs after AddDispatching and AddPersistence.");
                    var dispatcher = scope.ServiceProvider.GetRequiredService<IDispatcher>();
                    await unitOfWork.RunAsync(token => dispatcher.SendAsync(payload, token).AsTask(), cancel.Token).ConfigureAwait(false);
                    if (!context.Completed)
                    {
                        throw new InvalidOperationException(
                            $"Job kind '{kind.Name}' is not a command that runs in a unit of work; register AddJobs after AddDispatching and AddPersistence.");
                    }

                    break;
                case JobKindType.Batch:
                    var batch = Batches.GetOrAdd((kind.PayloadType, kind.CheckpointType!), key =>
                        BatchMethod.MakeGenericMethod(key.Payload, key.Checkpoint)
                            .CreateDelegate<Func<IServiceProvider, JobKind, JobContext, object, string?, CancellationToken, Task>>());
                    await batch(scope.ServiceProvider, kind, context, payload, claimed.Checkpoint, cancel.Token).ConfigureAwait(false);
                    break;
                case JobKindType.Rebuild:
                    var rebuild = Rebuilds.GetOrAdd((kind.PayloadType, module.Kinds[kind.EventKind!].PayloadType), key =>
                        RebuildMethod.MakeGenericMethod(key.Payload, key.Event)
                            .CreateDelegate<Func<IServiceProvider, JobKind, JobContext, object, CancellationToken, Task>>());
                    await rebuild(scope.ServiceProvider, kind, context, payload, cancel.Token).ConfigureAwait(false);
                    break;
                default:
                    var handle = Events.GetOrAdd(kind.PayloadType, key =>
                        EventMethod.MakeGenericMethod(key).CreateDelegate<Func<object, object, CancellationToken, Task>>());
                    await scope.ServiceProvider.GetRequiredService<IOuterPipeline>().RunAsync(payload, MessageKind.Event, async outerToken =>
                    {
                        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
                        {
                            await handle(scope.ServiceProvider.GetRequiredService(kind.HandlerType!), payload, token).ConfigureAwait(false);
                            await scope.ServiceProvider.GetRequiredService<EventPublisher>().PublishTrackedAsync(token).ConfigureAwait(false);
                            await context.CompleteAsync(scope.ServiceProvider.GetRequiredService<IJobWriter>(), token).ConfigureAwait(false);
                        }, outerToken).ConfigureAwait(false);
                        return true;
                    }, cancel.Token).ConfigureAwait(false);
                    break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await _transport.RequeueAsync(module, claimed.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await _transport.MarkCanceledAsync(module, claimed.Id, CancellationToken.None).ConfigureAwait(false);
        }
        catch (JobNoLongerRunningException exception)
        {
            LogNoLongerRunning(_logger, module.Name, claimed.Id, claimed.Kind, exception.Attempt);
        }
        catch (Exception exception)
        {
            var error = ErrorFor(exception, _catalog);
            var retry = exception is not (MessageValidationException or DomainException or StaleVersionException) && claimed.Attempts < claimed.MaxAttempts;
            if (error.Type == UnexpectedError)
            {
                LogJobFailed(_logger, exception, module.Name, claimed.Id, claimed.Kind, claimed.Attempts);
            }

            await _transport.RetryOrFailAsync(module, claimed.Id, error, retry ? RetryDelay(exception, claimed.Attempts) : null, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await heartbeatStop.CancelAsync().ConfigureAwait(false);
            await heartbeat.ConfigureAwait(false);
            await cancelWatch.ConfigureAwait(false);
        }
    }

    public async Task SweepStaleAsync(string module, CancellationToken cancellationToken = default)
    {
        var jobs = _registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
        await _transport.SweepStaleAsync(jobs, _options.StaleAfter, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> PruneAsync(string module, CancellationToken cancellationToken = default)
    {
        var jobs = _registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
        return await _transport.DeleteFinishedBeforeAsync(jobs, jobs.Retention ?? _options.FinishedJobRetention, cancellationToken).ConfigureAwait(false);
    }

    private static async Task RunBatchesAsync<TCommand, TCheckpoint>(
        IServiceProvider scope, JobKind kind, JobContext context, object payload, string? stored, CancellationToken cancellationToken)
        where TCommand : notnull
        where TCheckpoint : class
    {
        var handler = (IBatchJobHandler<TCommand, TCheckpoint>)scope.GetRequiredService(kind.HandlerType!);
        var unitOfWork = scope.GetRequiredService<IUnitOfWork>();
        var writer = scope.GetRequiredService<IJobWriter>();
        var publisher = scope.GetRequiredService<EventPublisher>();
        var tracker = scope.GetRequiredService<AggregateTracker>();
        var command = (TCommand)payload;
        var checkpoint = stored is null ? null : JsonSerializer.Deserialize<TCheckpoint>(stored, JobQueue.Json);
        await scope.GetRequiredService<IOuterPipeline>().RunAsync(command, MessageKind.Command, async outerToken =>
        {
            await LoopAsync(outerToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);

        async Task LoopAsync(CancellationToken loopToken)
        {
            while (true)
            {
                loopToken.ThrowIfCancellationRequested();
                BatchStep<TCheckpoint> step;
                if (handler.WritesNothing(command, checkpoint))
                {
                    var computed = await handler.RunBatchAsync(command, checkpoint, loopToken).ConfigureAwait(false);
                    if (tracker.HasTracked)
                    {
                        throw new InvalidOperationException(
                            $"Job kind '{kind.Name}' changed an aggregate in a batch that writes nothing; write it in the next batch.");
                    }

                    step = await unitOfWork.RunAsync(token => SaveAsync(computed, token), loopToken).ConfigureAwait(false);
                }
                else
                {
                    step = await unitOfWork.RunAsync(
                        async token => await SaveAsync(await handler.RunBatchAsync(command, checkpoint, token).ConfigureAwait(false), token).ConfigureAwait(false),
                        loopToken).ConfigureAwait(false);
                }

                if (step.Done)
                {
                    return;
                }

                checkpoint = step.Checkpoint;
            }
        }

        async Task<BatchStep<TCheckpoint>> SaveAsync(BatchStep<TCheckpoint> next, CancellationToken token)
        {
            await writer.SaveCheckpointAsync(context.JobsModule, context.JobId, JsonSerializer.Serialize(next.Checkpoint, JobQueue.Json), next.Progress, token).ConfigureAwait(false);
            await publisher.PublishTrackedAsync(token).ConfigureAwait(false);
            if (next.Done)
            {
                await context.CompleteAsync(writer, token).ConfigureAwait(false);
            }

            return next;
        }
    }

    private static async Task RebuildAsync<TCommand, TEvent>(
        IServiceProvider scope, JobKind kind, JobContext context, object payload, CancellationToken cancellationToken)
        where TCommand : notnull
    {
        var handler = (IRebuildJobHandler<TCommand, TEvent>)scope.GetRequiredService(kind.HandlerType!);
        var writer = scope.GetRequiredService<IJobWriter>();
        var publisher = scope.GetRequiredService<EventPublisher>();
        await scope.GetRequiredService<IOuterPipeline>().RunAsync((TCommand)payload, MessageKind.Command, async outerToken =>
        {
            await scope.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
            {
                var includes = await handler.RebuildAsync((TCommand)payload, token).ConfigureAwait(false);
                var deliveries = await writer.LockActiveAsync(context.JobsModule, kind.EventKind!, token).ConfigureAwait(false);
                var included = deliveries
                    .Where(delivery => includes(JsonSerializer.Deserialize<TEvent>(delivery.Payload, JobQueue.Json)
                        ?? throw new InvalidOperationException($"Delivery {delivery.Id} has an empty payload.")))
                    .Select(delivery => delivery.Id)
                    .ToArray();
                await writer.MarkSucceededAsync(context.JobsModule, kind.EventKind!, included, token).ConfigureAwait(false);
                await publisher.PublishTrackedAsync(token).ConfigureAwait(false);
                await context.CompleteAsync(writer, token).ConfigureAwait(false);
            }, outerToken).ConfigureAwait(false);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    private static Task HandleEventAsync<TEvent>(object handler, object payload, CancellationToken cancellationToken) =>
        ((IEventHandler<TEvent>)handler).HandleAsync((TEvent)payload, cancellationToken);

    private async Task BeatAsync(JobsModule module, Guid id, CancellationTokenSource cancel, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (await _transport.HeartbeatAsync(module, id, stop).ConfigureAwait(false))
                    {
                        await cancel.CancelAsync().ConfigureAwait(false);
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    LogHeartbeatFailed(_logger, exception, module.Name, id);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    private async Task WatchForCancelAsync(JobsModule module, Guid id, CancellationTokenSource cancel, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.CancelCheckInterval, _time);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                try
                {
                    if (await _transport.IsCancelRequestedAsync(module, id, stop).ConfigureAwait(false))
                    {
                        await cancel.CancelAsync().ConfigureAwait(false);
                        return;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException || !stop.IsCancellationRequested)
                {
                    LogCancelCheckFailed(_logger, exception, module.Name, id);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
    }

    internal TimeSpan RetryDelay(Exception exception, int attempt)
    {
        var backoff = Backoff(attempt);
        if (exception is DependencyUnavailableException { RetryAfter: { } wait })
        {
            var asked = wait > _options.RetryAfterMaxDelay ? _options.RetryAfterMaxDelay : wait;
            return asked > backoff ? asked : backoff;
        }

        return backoff;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.RetryBaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
        var jitter = _options.RetryBaseDelay * (RandomNumberGenerator.GetInt32(1001) / 1000d);
        var delay = exponential + jitter;
        return delay > _options.RetryMaxDelay ? _options.RetryMaxDelay : delay;
    }

    private const string UnexpectedError = "/problems/jobs/failed";

    internal static JobError ErrorFor(Exception exception, ModuleCatalog? catalog) => exception switch
    {
        DomainException domain => new JobError(Claudalisk.AspNetCore.ProblemTypes.For(domain, catalog), domain.Title, domain.Title, domain.Extensions.Count == 0 ? null : domain.Extensions),
        MessageValidationException validation => new JobError("/problems/jobs/invalid", "The job's input is not valid.", validation.Message),
        StaleVersionException => new JobError("/problems/precondition-failed", "The resource changed since the job read it.", null),
        DependencyUnavailableException => new JobError("/problems/dependency-unavailable", "A service this job needs is unavailable.", null),
        _ => new JobError(UnexpectedError, "The job failed.", null),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Module}/{Id} ({Kind}) failed on attempt {Attempt}")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string module, Guid id, string kind, int attempt);

    [LoggerMessage(Level = LogLevel.Information, Message = "Job {Module}/{Id} ({Kind}) attempt {Attempt} was settled or taken over while it ran; its writes were rolled back and the row is left as it is")]
    private static partial void LogNoLongerRunning(ILogger logger, string module, Guid id, string kind, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The heartbeat of job {Module}/{Id} failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, string module, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cancel check of job {Module}/{Id} failed; it checks again on the next interval")]
    private static partial void LogCancelCheckFailed(ILogger logger, Exception exception, string module, Guid id);
}
