namespace Claudalisk.Jobs;

public enum JobStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Canceled,
}

public sealed record JobOptions(string Lane = JobOptions.DefaultLane, string? DedupeKey = null, DateTimeOffset? NotBefore = null, int MaxAttempts = 5)
{
    public const string DefaultLane = "default";
}

public sealed record JobError(string Type, string Title, string? Detail);

public sealed record JobRecord(
    Guid Id,
    string Kind,
    string Lane,
    JobStatus Status,
    int Attempts,
    int MaxAttempts,
    double? ProgressFraction,
    string? ProgressStep,
    string? Checkpoint,
    string? ResultLink,
    JobError? LastError,
    string? DedupeKey,
    bool CancelRequested,
    DateTimeOffset CreatedAt,
    DateTimeOffset NotBefore,
    DateTimeOffset? StartedAt,
    DateTimeOffset? HeartbeatAt,
    DateTimeOffset? FinishedAt);

public sealed record NewJob(string Kind, string Payload, JobOptions Options);

public sealed record ClaimedJob(Guid Id, string Kind, string Payload, int Attempts, int MaxAttempts, string? Checkpoint);

public sealed record BatchStep<TCheckpoint>(TCheckpoint Checkpoint, bool Done, double? Progress = null)
    where TCheckpoint : class;

public sealed record JobRun(JobRecord Job, string Payload);

public sealed record JobHistoryQuery(string? Kind = null, IReadOnlyCollection<JobStatus>? Statuses = null, int Page = 1, int PageSize = 50);

public sealed record JobHistoryPage(IReadOnlyList<JobRun> Runs, int Total);

public interface IJobQueue
{
    Task<Claudalisk.Dispatching.JobReference> EnqueueAsync(object command, JobOptions? options = null, CancellationToken cancellationToken = default);
}

public interface IJobContext
{
    Guid JobId { get; }

    string Module { get; }

    int Attempt { get; }

    void SetResult(string href);

    Task ReportProgressAsync(double fraction, string? step, CancellationToken cancellationToken = default);
}

public interface IBatchJobHandler<in TCommand, TCheckpoint>
    where TCheckpoint : class
{
    Task<BatchStep<TCheckpoint>> RunBatchAsync(TCommand command, TCheckpoint? checkpoint, CancellationToken cancellationToken);

    bool WritesNothing(TCommand command, TCheckpoint? checkpoint) => false;
}

public interface IEventHandler<in TEvent>
{
    Task HandleAsync(TEvent integrationEvent, CancellationToken cancellationToken);
}

public interface IAggregateTracker
{
    void Track(Claudalisk.Domain.AggregateRoot aggregate);
}

public interface IJobHistory
{
    Task<JobHistoryPage> ListAsync(string module, JobHistoryQuery query, CancellationToken cancellationToken = default);

    Task<bool> DeleteFinishedAsync(string module, Guid id, CancellationToken cancellationToken = default);
}

public interface IEventPublisher
{
    Task PublishAsync(object integrationEvent, CancellationToken cancellationToken = default);
}

public interface IJobTransport
{
    Task<ClaimedJob?> ClaimAsync(JobsModule module, string lane, TimeSpan staleAfter, CancellationToken cancellationToken);

    Task WaitForWorkAsync(JobsModule module, string lane, TimeSpan timeout, CancellationToken cancellationToken);

    Task<bool> HeartbeatAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task<bool> IsCancelRequestedAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task ReportProgressAsync(JobsModule module, Guid id, double fraction, string? step, CancellationToken cancellationToken);

    Task RetryOrFailAsync(JobsModule module, Guid id, JobError error, TimeSpan? retryAfter, CancellationToken cancellationToken);

    Task MarkCanceledAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task RequeueAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task<JobRecord?> GetAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task<JobRecord?> RequestCancelAsync(JobsModule module, Guid id, CancellationToken cancellationToken);

    Task<JobHistoryPage> ListAsync(JobsModule module, JobHistoryQuery query, CancellationToken cancellationToken);

    Task<int> DeleteFinishedBeforeAsync(JobsModule module, TimeSpan retention, CancellationToken cancellationToken);
}

public interface IJobWriter
{
    Task<Guid> InsertAsync(JobsModule module, NewJob job, CancellationToken cancellationToken);

    Task CompleteAsync(JobsModule module, Guid id, string? resultLink, CancellationToken cancellationToken);

    Task SaveCheckpointAsync(JobsModule module, Guid id, string checkpoint, double? progress, CancellationToken cancellationToken);

    Task<bool> DeleteFinishedAsync(JobsModule module, Guid id, CancellationToken cancellationToken);
}
