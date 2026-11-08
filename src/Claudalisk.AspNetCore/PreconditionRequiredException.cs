namespace Claudalisk.AspNetCore;

public sealed class PreconditionRequiredException : Exception
{
    public PreconditionRequiredException()
        : this("This write needs an If-Match header with the version you last read.")
    {
    }

    public PreconditionRequiredException(string message)
        : base(message)
    {
    }

    public PreconditionRequiredException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
