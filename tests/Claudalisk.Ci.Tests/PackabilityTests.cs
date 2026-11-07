using System.Diagnostics;

namespace Claudalisk.Ci.Tests;

public sealed class PackabilityTests : IDisposable
{
    private const string PackableProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <IsPackable>true</IsPackable>
          </PropertyGroup>
        </Project>
        """;

    private readonly DirectoryInfo _fixture = Directory.CreateDirectory(
        Path.Combine(AppContext.BaseDirectory, "pack-fixtures", Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task PackRefusesAPackableProjectWithNoSourceFiles()
    {
        var project = WriteProject("Claudalisk.EmptyFixture");

        var result = await PackAsync(project);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("CLAUDALISK0001", result.Output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackAcceptsAPackableProjectWithSourceFiles()
    {
        var project = WriteProject("Claudalisk.ContentFixture");
        File.WriteAllText(Path.Combine(project.DirectoryName!, "Marker.cs"), "namespace Claudalisk.ContentFixture;\n\npublic sealed class Marker;\n");

        var result = await PackAsync(project);

        Assert.True(result.ExitCode == 0, result.Output);
        Assert.Single(Directory.GetFiles(Path.Combine(_fixture.FullName, "out"), "Claudalisk.ContentFixture.*.nupkg"));
    }

    public void Dispose() => _fixture.Delete(recursive: true);

    private FileInfo WriteProject(string name)
    {
        var directory = _fixture.CreateSubdirectory(name);
        var project = new FileInfo(Path.Combine(directory.FullName, $"{name}.csproj"));
        File.WriteAllText(project.FullName, PackableProject);
        return project;
    }

    private async Task<(int ExitCode, string Output)> PackAsync(FileInfo project)
    {
        var startInfo = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in new[] { "pack", project.FullName, "--output", Path.Combine(_fixture.FullName, "out"), "--disable-build-servers", "-nodeReuse:false" })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)!;
        var cancellationToken = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        return (process.ExitCode, await output + await error);
    }
}
