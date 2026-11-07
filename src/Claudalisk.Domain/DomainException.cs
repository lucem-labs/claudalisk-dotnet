using System.Collections.ObjectModel;
using System.Text.RegularExpressions;

namespace Claudalisk.Domain;

public abstract partial class DomainException : Exception
{
    private static readonly string[] ProblemDetailsMembers = ["type", "title", "status", "detail", "instance", "errors", "traceId"];

    private static readonly IReadOnlyDictionary<string, object?> NoExtensions =
        new ReadOnlyDictionary<string, object?>(new Dictionary<string, object?>());

    protected DomainException(
        string slug,
        string title,
        DomainErrorKind kind,
        IReadOnlyDictionary<string, object?>? extensions = null,
        Exception? innerException = null)
        : base(title, innerException)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        if (!SlugPattern().IsMatch(slug ?? ""))
        {
            throw new ArgumentException($"A problem slug is lower-case words joined by hyphens, such as 'already-shipped'; got '{slug}'.", nameof(slug));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown domain error kind.");
        }

        Slug = slug!;
        Title = title;
        Kind = kind;
        Extensions = extensions is null || extensions.Count == 0
            ? NoExtensions
            : new ReadOnlyDictionary<string, object?>(CopyExtensions(extensions));
    }

    public string Slug { get; }

    public string Title { get; }

    public DomainErrorKind Kind { get; }

    public IReadOnlyDictionary<string, object?> Extensions { get; }

    private static Dictionary<string, object?> CopyExtensions(IReadOnlyDictionary<string, object?> extensions)
    {
        var copy = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in extensions)
        {
            if (ProblemDetailsMembers.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                throw new ArgumentException($"The extension member '{name}' would replace a Problem Details member.", nameof(extensions));
            }

            if (!copy.TryAdd(name, value))
            {
                throw new ArgumentException($"The extension member '{name}' is given twice, ignoring case.", nameof(extensions));
            }
        }

        return copy;
    }

    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$")]
    private static partial Regex SlugPattern();
}
