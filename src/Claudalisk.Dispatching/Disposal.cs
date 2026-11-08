namespace Claudalisk.Dispatching;

internal static class Disposal
{
    public static async ValueTask DisposeAsync(object instance)
    {
        if (instance is IAsyncDisposable asyncDisposable)
        {
            await asyncDisposable.DisposeAsync();
        }
        else if (instance is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
