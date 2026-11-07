using System.Xml.Linq;

namespace Claudalisk.Ci.Tests;

public sealed class SolutionTests
{
    private static readonly DirectoryInfo RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void EveryProjectIsInTheSolution()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryRoot.FullName, "Claudalisk.Dotnet.slnx"));
        var listed = solution.Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = ProjectPaths("src").Concat(ProjectPaths("tests"))
            .Where(path => !listed.Contains(path))
            .ToList();

        Assert.NotEmpty(listed);
        Assert.Empty(missing);
    }

    private static IEnumerable<string> ProjectPaths(string folder)
    {
        var directory = new DirectoryInfo(Path.Combine(RepositoryRoot.FullName, folder));
        return directory.Exists
            ? directory.EnumerateFiles("*.csproj", SearchOption.AllDirectories)
                .Select(file => Path.GetRelativePath(RepositoryRoot.FullName, file.FullName).Replace('\\', '/'))
                .Where(path => !IsBuildOutput(path))
            : [];
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.Split('/').Any(part => part is "bin" or "obj");

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
