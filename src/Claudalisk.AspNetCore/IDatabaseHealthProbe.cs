namespace Claudalisk.AspNetCore;

public interface IDatabaseHealthProbe
{
    Task<bool> CanConnectAsync(CancellationToken cancellationToken = default);
}
