namespace Claudalisk.Domain.Tests;

public sealed class PackageReferenceTests
{
    [Fact]
    public void TheDomainAssemblyReferencesOnlyTheBaseLibrary()
    {
        var references = typeof(AggregateRoot).Assembly.GetReferencedAssemblies()
            .Select(reference => reference.Name!)
            .ToList();

        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name == "System" || name.StartsWith("System.", StringComparison.Ordinal),
            $"{name} is not part of the .NET base library."));
    }
}
