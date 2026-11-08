using System.Reflection;

namespace Claudalisk.Dispatching;

internal sealed class DispatchingState
{
    public HashSet<Assembly> Scanned { get; } = [];
}
