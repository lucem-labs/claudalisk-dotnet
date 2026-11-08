namespace Claudalisk.Dispatching;

internal static class PipelineSteps
{
    public static void RejectRetrySlot(IEnumerable<IPipelineStep> steps)
    {
        var offenders = steps.Where(step => step.Order == PipelineOrder.Retry).Select(step => $"'{step.GetType().FullName}'").ToArray();
        if (offenders.Length > 0)
        {
            throw new InvalidOperationException(
                $"{string.Join(", ", offenders)} has Order {PipelineOrder.Retry}, which belongs to the dispatcher's retry; give the step another Order.");
        }
    }
}
