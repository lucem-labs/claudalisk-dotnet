namespace Claudalisk.Domain.Tests;

public sealed class DependencyUnavailableExceptionTests
{
    [Fact]
    public void IsNotADomainException()
    {
        Assert.False(typeof(DomainException).IsAssignableFrom(typeof(DependencyUnavailableException)));
    }

    [Fact]
    public void HasNoWaitByDefault()
    {
        var error = new DependencyUnavailableException("The carrier API is down.");

        Assert.Equal("The carrier API is down.", error.Message);
        Assert.Null(error.RetryAfter);
    }

    [Fact]
    public void CarriesHowLongToWait()
    {
        var error = new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(30));

        Assert.Equal(TimeSpan.FromSeconds(30), error.RetryAfter);
    }

    [Fact]
    public void KeepsTheInnerException()
    {
        var cause = new HttpRequestException("503");

        var error = new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(5), cause);

        Assert.Same(cause, error.InnerException);
        Assert.Same(cause, new DependencyUnavailableException("The carrier API is down.", cause).InnerException);
    }

    [Fact]
    public void KeepsAZeroWait()
    {
        var error = new DependencyUnavailableException("The carrier API is down.", TimeSpan.Zero);

        Assert.Equal(TimeSpan.Zero, error.RetryAfter);
    }

    [Fact]
    public void RefusesANegativeWait()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DependencyUnavailableException("The carrier API is down.", TimeSpan.FromSeconds(-1)));
    }
}
