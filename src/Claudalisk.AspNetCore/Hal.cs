namespace Claudalisk.AspNetCore;

public static class Hal
{
    public const string LinksMember = "_links";

    public const string ActionsMember = "_actions";

    public static HalLinks Self(string href) => new HalLinks().With("self", href);

    public static HalActions Actions() => new();
}

public sealed record HalLink(string Href);

public sealed record HalAction(string Href, string Method);

public sealed class HalLinks : Dictionary<string, HalLink>
{
    public HalLinks()
        : base(StringComparer.Ordinal)
    {
    }

    public HalLinks With(string relation, string href)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        this[relation] = new HalLink(href);
        return this;
    }

    public HalLinks WithIf(bool present, string relation, string href) => present ? With(relation, href) : this;
}

public sealed class HalActions : Dictionary<string, HalAction>
{
    public HalActions()
        : base(StringComparer.Ordinal)
    {
    }

    public HalActions With(string name, string href, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        this[name] = new HalAction(href, method.ToUpperInvariant());
        return this;
    }

    public HalActions WithIf(bool allowed, string name, string href, string method) => allowed ? With(name, href, method) : this;
}
