using Claudalisk.Dispatching;

namespace Claudalisk.Jobs;

internal sealed class JobsPipelineStep(JobContext job, EventPublisher publisher, AggregateTracker tracker, IJobWriter writer) : IPipelineStep
{
    private int _depth;

    public async Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        if (context.Kind != MessageKind.Command)
        {
            return await next(cancellationToken);
        }

        _depth++;
        try
        {
            var result = await next(cancellationToken);
            if (_depth == 1)
            {
                await publisher.PublishTrackedAsync(cancellationToken);
                if (job.IsActive && !job.Completed && ReferenceEquals(context.Message, job.Command))
                {
                    await writer.CompleteAsync(job.JobsModule, job.JobId, job.ResultLink, cancellationToken);
                    job.Completed = true;
                }
            }

            return result;
        }
        catch
        {
            if (_depth == 1)
            {
                tracker.Forget();
            }

            throw;
        }
        finally
        {
            _depth--;
        }
    }
}
