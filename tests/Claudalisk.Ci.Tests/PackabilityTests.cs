using System.Xml.Linq;

namespace Claudalisk.Ci.Tests;

public sealed class PackabilityTests
{
    private static readonly DirectoryInfo RepositoryRoot = FindRepositoryRoot();

    [Fact]
    public void ProjectsAreNotPackableByDefault()
    {
        var props = XDocument.Load(Path.Combine(RepositoryRoot.FullName, "Directory.Build.props"));

        Assert.Equal(["false"], PropertyValues(props, "IsPackable"));
    }

    [Fact]
    public void OnlyPackagesWithSourceFilesArePackable()
    {
        var emptyPackableProjects = SourceProjects()
            .Where(project => PropertyValues(XDocument.Load(project.FullName), "IsPackable").Contains("true", StringComparer.OrdinalIgnoreCase))
            .Where(project => !HasSourceFiles(project.Directory!))
            .Select(project => Path.GetRelativePath(RepositoryRoot.FullName, project.FullName))
            .ToList();

        Assert.Empty(emptyPackableProjects);
    }

    [Fact]
    public void EveryProjectIsInTheSolution()
    {
        var solution = XDocument.Load(Path.Combine(RepositoryRoot.FullName, "Claudalisk.Dotnet.slnx"));
        var listed = solution.Descendants("Project")
            .Select(project => project.Attribute("Path")!.Value.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);

        var missing = ProjectFiles("src").Concat(ProjectFiles("tests"))
            .Select(project => Path.GetRelativePath(RepositoryRoot.FullName, project.FullName).Replace('\\', '/'))
            .Where(path => !listed.Contains(path))
            .ToList();

        Assert.Empty(missing);
    }

    private static IEnumerable<FileInfo> SourceProjects() => ProjectFiles("src");

    private static IEnumerable<FileInfo> ProjectFiles(string folder)
    {
        var directory = new DirectoryInfo(Path.Combine(RepositoryRoot.FullName, folder));
        return directory.Exists
            ? directory.EnumerateFiles("*.csproj", SearchOption.AllDirectories).Where(file => !IsBuildOutput(file.Directory!))
            : [];
    }

    private static bool HasSourceFiles(DirectoryInfo projectDirectory) =>
        projectDirectory.EnumerateFiles("*.cs", SearchOption.AllDirectories).Any(file => !IsBuildOutput(file.Directory!));

    private static bool IsBuildOutput(DirectoryInfo directory) =>
        directory.FullName.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(part => part is "bin" or "obj");

    private static string[] PropertyValues(XDocument project, string name) =>
        project.Descendants("PropertyGroup").Elements(name).Select(element => element.Value.Trim()).ToArray();

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
