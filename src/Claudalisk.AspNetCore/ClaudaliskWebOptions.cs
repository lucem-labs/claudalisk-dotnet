namespace Claudalisk.AspNetCore;

public sealed class ClaudaliskWebOptions
{
    public Func<Type, string?>? TagFor { get; set; }

    public IDictionary<string, string> TagDescriptions { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

    public TimeSpan DefaultRetryAfter { get; set; } = TimeSpan.FromSeconds(30);
}
