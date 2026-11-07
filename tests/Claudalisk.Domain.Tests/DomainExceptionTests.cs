namespace Claudalisk.Domain.Tests;

public sealed class DomainExceptionTests
{
    [Theory]
    [InlineData(DomainErrorKind.Conflict)]
    [InlineData(DomainErrorKind.NotFound)]
    [InlineData(DomainErrorKind.Forbidden)]
    public void CarriesItsSlugTitleAndKind(DomainErrorKind kind)
    {
        var error = new OrderError("already-shipped", "The order has already shipped.", kind);

        Assert.Equal("already-shipped", error.Slug);
        Assert.Equal("The order has already shipped.", error.Title);
        Assert.Equal("The order has already shipped.", error.Message);
        Assert.Equal(kind, error.Kind);
        Assert.Empty(error.Extensions);
    }

    [Fact]
    public void CarriesExtensionMembersThatLaterChangesToTheSourceDoNotReach()
    {
        var source = new Dictionary<string, object?> { ["available"] = 2, ["requested"] = null };

        var error = new OrderError("not-enough-stock", "Only 2 are left.", DomainErrorKind.Conflict, source);
        source["available"] = 5;

        Assert.Equal(2, error.Extensions["available"]);
        Assert.True(error.Extensions.ContainsKey("requested"));
        Assert.Null(error.Extensions["requested"]);
        Assert.False(error.Extensions is IDictionary<string, object?> { IsReadOnly: false });
    }

    [Theory]
    [InlineData("type")]
    [InlineData("title")]
    [InlineData("status")]
    [InlineData("detail")]
    [InlineData("instance")]
    [InlineData("errors")]
    [InlineData("traceId")]
    [InlineData("Title")]
    public void RefusesAnExtensionMemberThatWouldReplaceAProblemDetailsMember(string name)
    {
        var error = Assert.Throws<ArgumentException>(() => new OrderError("already-shipped", "Title.", DomainErrorKind.Conflict, new Dictionary<string, object?> { [name] = "x" }));

        Assert.Equal("extensions", error.ParamName);
    }

    [Fact]
    public void RefusesExtensionMembersWhoseNamesDifferOnlyInCase()
    {
        var error = Assert.Throws<ArgumentException>(() => new OrderError("not-enough-stock", "Title.", DomainErrorKind.Conflict, new Dictionary<string, object?> { ["available"] = 3, ["Available"] = 4 }));

        Assert.Equal("extensions", error.ParamName);
    }

    [Fact]
    public void KeepsTheInnerException()
    {
        var inner = new InvalidOperationException("cause");

        var error = new OrderError("not-found", "No such order.", DomainErrorKind.NotFound, innerException: inner);

        Assert.Same(inner, error.InnerException);
    }

    [Fact]
    public void IsAnExceptionSoItPropagatesToTheUnitOfWork()
    {
        Action cancelMissingOrder = () => throw new OrderError("not-found", "No such order.", DomainErrorKind.NotFound);

        Assert.Throws<OrderError>(cancelMissingOrder);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Already-Shipped")]
    [InlineData("already shipped")]
    [InlineData("already_shipped")]
    [InlineData("-already")]
    [InlineData("already-")]
    [InlineData("already--shipped")]
    public void RefusesASlugThatIsNotLowerCaseWordsJoinedByHyphens(string slug)
    {
        var error = Assert.Throws<ArgumentException>(() => new OrderError(slug, "Title.", DomainErrorKind.Conflict));

        Assert.Equal("slug", error.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void RefusesABlankTitle(string title)
    {
        Assert.ThrowsAny<ArgumentException>(() => new OrderError("not-found", title, DomainErrorKind.NotFound));
    }

    [Fact]
    public void RefusesAnUnknownKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new OrderError("not-found", "No such order.", (DomainErrorKind)42));
    }

    private sealed class OrderError(
        string slug,
        string title,
        DomainErrorKind kind,
        IReadOnlyDictionary<string, object?>? extensions = null,
        Exception? innerException = null)
        : DomainException(slug, title, kind, extensions, innerException);
}
