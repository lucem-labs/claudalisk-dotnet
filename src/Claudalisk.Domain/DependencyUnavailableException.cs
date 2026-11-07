namespace Claudalisk.Domain;

public sealed class DependencyUnavailableException : Exception
{
    public DependencyUnavailableException()
        : this("A dependency is unavailable.")
    {
    }

    public DependencyUnavailableException(string message)
        : this(message, retryAfter: null, innerException: null)
    {
    }

    public DependencyUnavailableException(string message, Exception innerException)
        : this(message, retryAfter: null, innerException)
    {
    }

    public DependencyUnavailableException(string message, TimeSpan? retryAfter, Exception? innerException = null)
        : base(message, innerException)
    {
        if (retryAfter is { } wait && wait < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), retryAfter, "The wait before a retry cannot be negative.");
        }

        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
