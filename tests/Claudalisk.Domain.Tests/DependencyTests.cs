namespace Claudalisk.Domain.Tests;

public sealed class DependencyTests
{
    [Fact]
    public void TheLibraryReferencesOnlyTheBaseLibrary()
    {
        var references = typeof(DomainException).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToArray();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name is "netstandard" or "mscorlib" || name.StartsWith("System.", StringComparison.Ordinal) || name == "System",
            $"Claudalisk.Domain references {name}."));
    }
}
