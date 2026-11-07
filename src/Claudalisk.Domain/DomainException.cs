using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Claudalisk.Domain;

public abstract partial class DomainException : Exception
{
    private static readonly string[] ProblemDetailsMembers = ["type", "title", "status", "detail", "instance", "errors", "traceId"];

    private static readonly IReadOnlyDictionary<string, object?> NoExtensions =
        ReadOnlyDictionary<string, object?>.Empty;

    protected DomainException(
        string slug,
        string title,
        DomainErrorKind kind,
        IReadOnlyDictionary<string, object?>? extensions = null,
        Exception? innerException = null)
        : base(title, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(slug);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);

        if (!SlugPattern().IsMatch(slug))
        {
            throw new ArgumentException($"The problem slug '{slug}' must be lowercase words joined by hyphens, such as 'already-shipped'.", nameof(slug));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "The kind must be Conflict, NotFound or Forbidden.");
        }

        Slug = slug;
        Title = title;
        Kind = kind;
        Extensions = CopyExtensions(extensions);
    }

    public string Slug { get; }

    public string Title { get; }

    public DomainErrorKind Kind { get; }

    public IReadOnlyDictionary<string, object?> Extensions { get; }

    private static IReadOnlyDictionary<string, object?> CopyExtensions(IReadOnlyDictionary<string, object?>? extensions)
    {
        if (extensions is null || extensions.Count == 0)
        {
            return NoExtensions;
        }

        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in extensions)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("An extension member needs a name.", nameof(extensions));
            }

            if (ProblemDetailsMembers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The extension member '{name}' would replace a Problem Details member.", nameof(extensions));
            }

            if (!copy.TryAdd(name, value))
            {
                throw new ArgumentException($"The extension member '{name}' is given twice, ignoring case.", nameof(extensions));
            }
        }

        return copy.AsReadOnly();
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex SlugPattern();
}
