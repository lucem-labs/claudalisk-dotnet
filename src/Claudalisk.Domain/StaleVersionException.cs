using System.Globalization;

namespace Claudalisk.Domain;

public sealed class StaleVersionException : Exception
{
    public StaleVersionException()
        : this("The resource changed since it was read. Read it again.")
    {
    }

    public StaleVersionException(string message)
        : base(message)
    {
    }

    public StaleVersionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public StaleVersionException(long expectedVersion, long actualVersion)
        : base(MessageFor(expectedVersion, actualVersion))
    {
        ExpectedVersion = expectedVersion;
        ActualVersion = actualVersion;
    }

    public long ExpectedVersion { get; }

    public long ActualVersion { get; }

    private static string MessageFor(long expectedVersion, long actualVersion) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"The resource is at version {actualVersion}, but the request expected version {expectedVersion}. Read it again.");
}
