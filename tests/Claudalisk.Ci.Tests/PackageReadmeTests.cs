using System.Diagnostics;
using System.Text.Json;

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

        Assert.NotEmpty(packable);
        Assert.Empty(missing);
    }

    [Fact(Timeout = 120_000)]
    public async Task ThePackWiringPutsTheReadmeIntoThePackage()
    {
        var project = Path.Combine(FindRepositoryRoot().FullName, "src", "Claudalisk.Domain", "Claudalisk.Domain.csproj");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "msbuild", project, "-getProperty:PackageReadmeFile", "-getItem:None" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(TestContext.Current.CancellationToken);
        await process.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(0, process.ExitCode);
        using var evaluation = JsonDocument.Parse(output);
        Assert.Equal("README.md", evaluation.RootElement.GetProperty("Properties").GetProperty("PackageReadmeFile").GetString());
        Assert.Contains(
            evaluation.RootElement.GetProperty("Items").GetProperty("None").EnumerateArray(),
            item => item.GetProperty("Identity").GetString() == "README.md"
                && item.TryGetProperty("Pack", out var pack) && pack.GetString() == "true"
                && item.GetProperty("PackagePath").GetString() == "/");
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
