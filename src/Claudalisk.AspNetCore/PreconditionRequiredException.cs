namespace Claudalisk.AspNetCore;

public sealed class PreconditionRequiredException()
    : Exception("This write needs an If-Match header with the version you last read.");
