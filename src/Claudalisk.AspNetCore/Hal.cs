using System.Collections;
using System.Diagnostics.CodeAnalysis;

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

public abstract class HalMemberDictionary<TValue> : IReadOnlyDictionary<string, TValue>
{
    private readonly Dictionary<string, TValue> _members = new(StringComparer.Ordinal);

    public int Count => _members.Count;

    public IEnumerable<string> Keys => _members.Keys;

    public IEnumerable<TValue> Values => _members.Values;

    public TValue this[string key] => _members[key];

    public bool ContainsKey(string key) => _members.ContainsKey(key);

    public bool TryGetValue(string key, [MaybeNullWhen(false)] out TValue value) => _members.TryGetValue(key, out value);

    public IEnumerator<KeyValuePair<string, TValue>> GetEnumerator() => _members.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private protected void Set(string key, TValue value) => _members[key] = value;
}

public sealed class HalLinks : HalMemberDictionary<HalLink>
{
    public void Add(string relation, string href) => With(relation, href);

    public HalLinks With(string relation, string href)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relation);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        Set(relation, new HalLink(href));
        return this;
    }

    public HalLinks WithIf(bool present, string relation, string href) => present ? With(relation, href) : this;
}

public sealed class HalActions : HalMemberDictionary<HalAction>
{
    public void Add(string name, string href, string method) => With(name, href, method);

    public HalActions With(string name, string href, string method)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(href);
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        Set(name, new HalAction(href, method.ToUpperInvariant()));
        return this;
    }

    public HalActions WithIf(bool allowed, string name, string href, string method) => allowed ? With(name, href, method) : this;
}
