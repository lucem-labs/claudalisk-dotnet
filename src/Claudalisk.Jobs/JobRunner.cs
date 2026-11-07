using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Claudalisk.Dispatching;
using Claudalisk.Domain;
using Claudalisk.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Claudalisk.Jobs;

public sealed partial class JobRunner
{
    private static readonly MethodInfo BatchMethod = typeof(JobRunner).GetMethod(nameof(RunBatchesAsync), BindingFlags.NonPublic | BindingFlags.Static)!;
    private static readonly MethodInfo EventMethod = typeof(JobRunner).GetMethod(nameof(HandleEventAsync), BindingFlags.NonPublic | BindingFlags.Static)!;

    private readonly IServiceScopeFactory _scopes;
    private readonly IJobTransport _transport;
    private readonly JobsRegistry _registry;
    private readonly JobsOptions _options;
    private readonly ILogger<JobRunner> _logger;

    internal JobRunner(IServiceScopeFactory scopes, IJobTransport transport, JobsRegistry registry, IOptions<JobsOptions> options, ILogger<JobRunner> logger)
    {
        _scopes = scopes;
        _transport = transport;
        _registry = registry;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<int> RunUntilIdleAsync(string module, string lane = JobOptions.DefaultLane, CancellationToken cancellationToken = default)
    {
        var ran = 0;
        while (await RunNextAsync(module, lane, cancellationToken))
        {
            ran++;
        }

        return ran;
    }

    public async Task<bool> RunNextAsync(string module, string lane = JobOptions.DefaultLane, CancellationToken cancellationToken = default)
    {
        var jobs = _registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
        var claimed = await _transport.ClaimAsync(jobs, lane, _options.StaleAfter, cancellationToken);
        if (claimed is null)
        {
            return false;
        }

        await RunAsync(jobs, claimed, cancellationToken);
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
            await using var scope = _scopes.CreateAsyncScope();
            var context = scope.ServiceProvider.GetRequiredService<JobContext>();
            context.Start(module, claimed, payload);
            switch (kind.Type)
            {
                case JobKindType.Command:
                    await scope.ServiceProvider.GetRequiredService<IDispatcher>().SendAsync(payload, cancel.Token);
                    if (!context.Completed)
                    {
                        throw new InvalidOperationException(
                            $"Job kind '{kind.Name}' is not a command that runs in a unit of work; register AddJobs after AddDispatching and AddPersistence.");
                    }

                    break;
                case JobKindType.Batch:
                    await (Task)BatchMethod.MakeGenericMethod(kind.PayloadType, kind.CheckpointType!)
                        .Invoke(null, [scope.ServiceProvider, kind, context, payload, claimed.Checkpoint, cancel.Token])!;
                    break;
                default:
                    await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().RunAsync(async token =>
                    {
                        await (Task)EventMethod.MakeGenericMethod(kind.PayloadType)
                            .Invoke(null, [scope.ServiceProvider.GetRequiredService(kind.HandlerType!), payload, token])!;
                        await scope.ServiceProvider.GetRequiredService<EventPublisher>().PublishTrackedAsync(token);
                        await scope.ServiceProvider.GetRequiredService<IJobWriter>().CompleteAsync(module, claimed.Id, context.ResultLink, token);
                    }, cancel.Token);
                    break;
            }
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
            await _transport.RequeueAsync(module, claimed.Id, CancellationToken.None);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            await _transport.MarkCanceledAsync(module, claimed.Id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            var error = ErrorFor(module, exception);
            var retry = exception is not (MessageValidationException or DomainException) && claimed.Attempts < claimed.MaxAttempts;
            if (error.Type == UnexpectedError)
            {
                LogJobFailed(_logger, exception, module.Name, claimed.Id, claimed.Kind, claimed.Attempts);
            }

            await _transport.RetryOrFailAsync(module, claimed.Id, error, retry ? RetryDelay(exception, claimed.Attempts) : null, CancellationToken.None);
        }
        finally
        {
            await heartbeatStop.CancelAsync();
            await heartbeat;
            await cancelWatch;
        }
    }

    public async Task<int> PruneAsync(string module, CancellationToken cancellationToken = default)
    {
        var jobs = _registry.Find(module) ?? throw new InvalidOperationException($"Module '{module}' has no jobs.");
        return await _transport.DeleteFinishedBeforeAsync(jobs, jobs.Retention ?? _options.FinishedJobRetention, cancellationToken);
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
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BatchStep<TCheckpoint> step;
            if (handler.WritesNothing(command, checkpoint))
            {
                var computed = await handler.RunBatchAsync(command, checkpoint, cancellationToken);
                if (tracker.HasTracked)
                {
                    throw new InvalidOperationException(
                        $"Job kind '{kind.Name}' changed an aggregate in a batch that writes nothing; write it in the next batch.");
                }

                step = await unitOfWork.RunAsync(token => SaveAsync(computed, token), cancellationToken);
            }
            else
            {
                step = await unitOfWork.RunAsync(
                    async token => await SaveAsync(await handler.RunBatchAsync(command, checkpoint, token), token),
                    cancellationToken);
            }

            if (step.Done)
            {
                return;
            }

            checkpoint = step.Checkpoint;
        }

        async Task<BatchStep<TCheckpoint>> SaveAsync(BatchStep<TCheckpoint> next, CancellationToken token)
        {
            await writer.SaveCheckpointAsync(context.JobsModule, context.JobId, JsonSerializer.Serialize(next.Checkpoint, JobQueue.Json), next.Progress, token);
            await publisher.PublishTrackedAsync(token);
            if (next.Done)
            {
                await writer.CompleteAsync(context.JobsModule, context.JobId, context.ResultLink, token);
            }

            return next;
        }
    }

    private static Task HandleEventAsync<TEvent>(object handler, object payload, CancellationToken cancellationToken) =>
        ((IEventHandler<TEvent>)handler).HandleAsync((TEvent)payload, cancellationToken);

    private async Task BeatAsync(JobsModule module, Guid id, CancellationTokenSource cancel, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.HeartbeatInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                if (await _transport.HeartbeatAsync(module, id, stop))
                {
                    await cancel.CancelAsync();
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            LogHeartbeatFailed(_logger, exception, module.Name, id);
        }
    }

    private async Task WatchForCancelAsync(JobsModule module, Guid id, CancellationTokenSource cancel, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(_options.CancelCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop))
            {
                try
                {
                    if (await _transport.IsCancelRequestedAsync(module, id, stop))
                    {
                        await cancel.CancelAsync();
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

    private TimeSpan RetryDelay(Exception exception, int attempt)
    {
        var backoff = Backoff(attempt);
        return exception is DependencyUnavailableException { RetryAfter: { } wait } && wait > backoff ? wait : backoff;
    }

    private TimeSpan Backoff(int attempt)
    {
        var exponential = _options.RetryBaseDelay * Math.Pow(2, Math.Max(0, attempt - 1));
        var jitter = _options.RetryBaseDelay * (RandomNumberGenerator.GetInt32(1001) / 1000d);
        var delay = exponential + jitter;
        return delay > _options.RetryMaxDelay ? _options.RetryMaxDelay : delay;
    }

    private const string UnexpectedError = "/problems/jobs/failed";

    internal static JobError ErrorFor(JobsModule module, Exception exception) => exception switch
    {
        DomainException domain => new JobError($"/problems/{Claudalisk.AspNetCore.NameSegments.Kebab(module.Name)}/{domain.Slug}", domain.Title, domain.Title),
        MessageValidationException validation => new JobError("/problems/jobs/invalid", "The job's input is not valid.", validation.Message),
        DependencyUnavailableException => new JobError("/problems/dependency-unavailable", "A service this job needs is unavailable.", null),
        _ => new JobError(UnexpectedError, "The job failed.", null),
    };

    [LoggerMessage(Level = LogLevel.Error, Message = "Job {Module}/{Id} ({Kind}) failed on attempt {Attempt}")]
    private static partial void LogJobFailed(ILogger logger, Exception exception, string module, Guid id, string kind, int attempt);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The heartbeat of job {Module}/{Id} failed")]
    private static partial void LogHeartbeatFailed(ILogger logger, Exception exception, string module, Guid id);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The cancel check of job {Module}/{Id} failed; it checks again on the next interval")]
    private static partial void LogCancelCheckFailed(ILogger logger, Exception exception, string module, Guid id);
}
