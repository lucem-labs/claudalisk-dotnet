using Claudalisk.Dispatching;

namespace Claudalisk.Jobs;

internal sealed class JobsPipelineStep(JobContext job, EventPublisher publisher, AggregateTracker tracker, IJobWriter writer) : IPipelineStep
{
    private int _depth;

    public int Order => PipelineOrder.UnitOfWork + 1000;

    public async Task<object?> InvokeAsync(MessageContext context, Func<CancellationToken, Task<object?>> next, CancellationToken cancellationToken)
    {
        if (context.Kind != MessageKind.Command)
        {
            return await next(cancellationToken).ConfigureAwait(false);
        }

        _depth++;
        try
        {
            var result = await next(cancellationToken).ConfigureAwait(false);
            if (_depth == 1)
            {
                await publisher.PublishTrackedAsync(cancellationToken).ConfigureAwait(false);
                if (job.IsActive && !job.Completed && ReferenceEquals(context.Message, job.Command))
                {
                    await job.CompleteAsync(writer, cancellationToken).ConfigureAwait(false);
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
