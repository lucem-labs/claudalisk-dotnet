namespace Claudalisk.Domain.Tests;

public sealed class StaleVersionExceptionTests
{
    [Fact]
    public void IsNotADomainException()
    {
        Assert.False(typeof(DomainException).IsAssignableFrom(typeof(StaleVersionException)));
    }

    [Fact]
    public void KeepsTheInnerException()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Same(inner, new StaleVersionException("Read it again.", inner).InnerException);
    }

    [Fact]
    public void HasAMessageByDefault()
    {
        Assert.False(string.IsNullOrWhiteSpace(new StaleVersionException().Message));
    }
}
