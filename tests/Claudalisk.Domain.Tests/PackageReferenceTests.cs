using System.Xml.Linq;

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

    [Fact]
    public void TheDomainProjectHasNoPackageOrProjectReferences()
    {
        var project = XDocument.Load(Path.Combine(FindRepositoryRoot().FullName, "src", "Claudalisk.Domain", "Claudalisk.Domain.csproj"));

        var references = project.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "ProjectReference" or "Reference" or "FrameworkReference")
            .Select(element => element.ToString())
            .ToList();

        Assert.Empty(references);
    }

    private static DirectoryInfo FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Claudalisk.Dotnet.slnx")))
            {
                return directory;
            }
        }

        throw new InvalidOperationException("Claudalisk.Dotnet.slnx was not found above the test output folder.");
    }
}
