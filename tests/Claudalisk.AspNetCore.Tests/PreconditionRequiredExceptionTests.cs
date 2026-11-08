namespace Claudalisk.AspNetCore.Tests;

public sealed class PreconditionRequiredExceptionTests
{
    [Fact]
    public void HasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        Assert.Contains("If-Match", new PreconditionRequiredException().Message, StringComparison.Ordinal);
        Assert.Equal("failed", new PreconditionRequiredException("failed").Message);
        Assert.Same(inner, new PreconditionRequiredException("failed", inner).InnerException);
    }
}
