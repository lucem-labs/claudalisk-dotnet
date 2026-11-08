namespace Claudalisk.Jobs;

internal sealed class JobNoLongerRunningException(string module, Guid id, int attempt)
    : Exception($"Job {module}/{id} is no longer running attempt {attempt}; a rebuild, a cancel or the stale sweep settled it.")
{
    public int Attempt { get; } = attempt;
}
