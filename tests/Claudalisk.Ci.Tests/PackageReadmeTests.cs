namespace Claudalisk.Ci.Tests;

public sealed class PackageReadmeTests
{
    [Fact]
    public void EveryPackableProjectHasAReadmeNextToItsProject()
    {
        var source = new DirectoryInfo(Path.Combine(FindRepositoryRoot().FullName, "src"));
        var packable = source.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
            .Where(file => !file.FullName.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj"))
            .Where(file => File.ReadAllText(file.FullName).Contains("<IsPackable>true</IsPackable>", StringComparison.Ordinal))
            .ToList();

        var missing = packable
            .Where(file => !File.Exists(Path.Combine(file.DirectoryName!, "README.md")))
            .Select(file => file.Name)
            .ToList();

        Assert.Equal(10, packable.Count);
        Assert.Empty(missing);
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
