namespace Claudalisk.Persistence.Tests;

public sealed class ExceptionConstructorTests
{
    [Fact]
    public void MigrationBaselineExceptionHasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new MigrationBaselineException().Message);
        Assert.Equal("failed", new MigrationBaselineException("failed").Message);
        Assert.Same(inner, new MigrationBaselineException("failed", inner).InnerException);
    }

    [Fact]
    public void MigrationHistoryMoveExceptionHasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new MigrationHistoryMoveException().Message);
        Assert.Equal("failed", new MigrationHistoryMoveException("failed").Message);
        Assert.Same(inner, new MigrationHistoryMoveException("failed", inner).InnerException);
    }

    [Fact]
    public void ConcurrencyConflictExceptionHasTheStandardConstructors()
    {
        var inner = new InvalidOperationException("inner");

        Assert.NotNull(new ConcurrencyConflictException().Message);
        Assert.Equal("failed", new ConcurrencyConflictException("failed").Message);
        Assert.Same(inner, new ConcurrencyConflictException("failed", inner).InnerException);
    }
}
