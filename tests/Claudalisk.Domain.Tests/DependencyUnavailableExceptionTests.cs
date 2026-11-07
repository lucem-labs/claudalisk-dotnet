namespace Claudalisk.Domain.Tests;

public sealed class DependencyUnavailableExceptionTests
{
    [Fact]
    public void ItIsNotADomainException()
    {
        Assert.False(typeof(DomainException).IsAssignableFrom(typeof(DependencyUnavailableException)));
    }

    [Fact]
    public void ItHasNoWaitByDefault()
    {
        var exception = new DependencyUnavailableException("The carrier API is down.");

        Assert.Equal("The carrier API is down.", exception.Message);
        Assert.Null(exception.RetryAfter);
    }

    [Fact]
    public void ItCarriesHowLongToWait()
    {
        var exception = new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), exception.RetryAfter);
    }

    [Fact]
    public void ItKeepsTheInnerException()
    {
        var cause = new HttpRequestException("503");

        var exception = new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(5), cause);

        Assert.Same(cause, exception.InnerException);
        Assert.Same(cause, new DependencyUnavailableException("The carrier API is down.", cause).InnerException);
    }

    [Fact]
    public void AZeroWaitIsKept()
    {
        var exception = new DependencyUnavailableException("The carrier API is down.", TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, exception.RetryAfter);
    }

    [Fact]
    public void ANegativeWaitIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(-1)));
    }
}
