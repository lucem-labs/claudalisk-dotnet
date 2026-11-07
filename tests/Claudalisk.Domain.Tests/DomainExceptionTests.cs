namespace Claudalisk.Domain.Tests;

public sealed class DomainExceptionTests
{
    [Theory]
    [InlineData(DomainErrorKind.Conflict)]
    [InlineData(DomainErrorKind.NotFound)]
    [InlineData(DomainErrorKind.Forbidden)]
    public void ItCarriesItsSlugTitleAndKind(DomainErrorKind kind)
    {
        var exception = new TestDomainException("already-shipped", "The order has already shipped.", kind);

        Assert.Equal("already-shipped", exception.Slug);
        Assert.Equal("The order has already shipped.", exception.Title);
        Assert.Equal(kind, exception.Kind);
    }

    [Fact]
    public void TheKindsAreConflictNotFoundAndForbidden()
    {
        Assert.Equal(
            [DomainErrorKind.Conflict, DomainErrorKind.NotFound, DomainErrorKind.Forbidden],
            Enum.GetValues<DomainErrorKind>());
    }

    [Fact]
    public void TheMessageIsTheTitle()
    {
        var exception = new TestDomainException("already-shipped", "The order has already shipped.", DomainErrorKind.Conflict);

        Assert.Equal("The order has already shipped.", exception.Message);
    }

    [Fact]
    public void ItHasNoExtensionMembersByDefault()
    {
        var exception = new TestDomainException("already-shipped", "The order has already shipped.", DomainErrorKind.Conflict);

        Assert.Empty(exception.Extensions);
    }

    [Fact]
    public void ItCarriesItsExtensionMembers()
    {
        var exception = new TestDomainException(
            "not-enough-stock",
            "Not enough items are in stock.",
            DomainErrorKind.Conflict,
            new Dictionary<string, object?> { ["available"] = 3, ["sku"] = "A-1", ["note"] = null });

        Assert.Equal(3, exception.Extensions["available"]);
        Assert.Equal("A-1", exception.Extensions["sku"]);
        Assert.Null(exception.Extensions["note"]);
        Assert.Equal(3, exception.Extensions.Count);
    }

    [Fact]
    public void ExtensionMembersAreCopiedWhenThrown()
    {
        var extensions = new Dictionary<string, object?> { ["available"] = 3 };
        var exception = new TestDomainException("not-enough-stock", "Not enough items are in stock.", DomainErrorKind.Conflict, extensions);

        extensions["available"] = 0;
        extensions["added"] = true;

        Assert.Equal(3, exception.Extensions["available"]);
        Assert.False(exception.Extensions.ContainsKey("added"));
    }

    [Fact]
    public void ExtensionMembersCannotBeChangedThroughTheException()
    {
        var exception = new TestDomainException(
            "not-enough-stock",
            "Not enough items are in stock.",
            DomainErrorKind.Conflict,
            new Dictionary<string, object?> { ["available"] = 3 });

        var asDictionary = Assert.IsAssignableFrom<IDictionary<string, object?>>(exception.Extensions);
        Assert.Throws<NotSupportedException>(() => asDictionary["available"] = 0);
    }

    [Theory]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("status")]
    [InlineData("detail")]
    [InlineData("instance")]
    [InlineData("Title")]
    public void AnExtensionMemberCannotReplaceAProblemDetailsMember(string name)
    {
        Assert.Throws<ArgumentException>(() => new TestDomainException(
            "already-shipped",
            "The order has already shipped.",
            DomainErrorKind.Conflict,
            new Dictionary<string, object?> { [name] = "x" }));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void AnExtensionMemberNeedsAName(string name)
    {
        Assert.Throws<ArgumentException>(() => new TestDomainException(
            "already-shipped",
            "The order has already shipped.",
            DomainErrorKind.Conflict,
            new Dictionary<string, object?> { [name] = "x" }));
    }

    [Theory]
    [InlineData("shipped")]
    [InlineData("already-shipped")]
    [InlineData("over-limit-2")]
    public void AKebabCaseSlugIsAccepted(string slug)
    {
        var exception = new TestDomainException(slug, "A title.", DomainErrorKind.Conflict);

        Assert.Equal(slug, exception.Slug);
    }

    [Theory]
    [InlineData("Already-Shipped")]
    [InlineData("already_shipped")]
    [InlineData("already shipped")]
    [InlineData("-already-shipped")]
    [InlineData("already-shipped-")]
    [InlineData("already--shipped")]
    [InlineData("orders/already-shipped")]
    public void ASlugThatIsNotKebabCaseIsRefused(string slug)
    {
        Assert.Throws<ArgumentException>(() => new TestDomainException(slug, "A title.", DomainErrorKind.Conflict));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ABlankSlugIsRefused(string slug)
    {
        Assert.Throws<ArgumentException>(() => new TestDomainException(slug, "A title.", DomainErrorKind.Conflict));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void ABlankTitleIsRefused(string title)
    {
        Assert.Throws<ArgumentException>(() => new TestDomainException("already-shipped", title, DomainErrorKind.Conflict));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void AnUndefinedKindIsRefused(int kind)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TestDomainException("already-shipped", "A title.", (DomainErrorKind)kind));
    }

    [Fact]
    public void ItKeepsTheInnerException()
    {
        var cause = new InvalidOperationException("cause");

        var exception = new TestDomainException("already-shipped", "A title.", DomainErrorKind.Conflict, innerException: cause);

        Assert.Same(cause, exception.InnerException);
    }
}
