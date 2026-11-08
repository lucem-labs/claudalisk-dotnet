using System.Diagnostics;
using System.Text.Json;

namespace Claudalisk.Ci.Tests;

public sealed class PackageReadmeTests
{
    [Fact(Timeout = 120_000)]
    public async Task EveryPackageRunsPackageValidation()
    {
        using var evaluation = await EvaluatePackWiringAsync("EnablePackageValidation", TestContext.Current.CancellationToken);

        Assert.Equal("true", evaluation.RootElement.GetProperty("Properties").GetProperty("EnablePackageValidation").GetString());
    }

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
        using var evaluation = await EvaluatePackWiringAsync("PackageReadmeFile", TestContext.Current.CancellationToken);

        Assert.Equal("README.md", evaluation.RootElement.GetProperty("Properties").GetProperty("PackageReadmeFile").GetString());
        Assert.Contains(
            evaluation.RootElement.GetProperty("Items").GetProperty("None").EnumerateArray(),
            item => item.GetProperty("Identity").GetString() == "README.md"
                && item.TryGetProperty("Pack", out var pack) && pack.GetString() == "true"
                && item.GetProperty("PackagePath").GetString() == "/");
    }

    [Fact(Timeout = 120_000)]
    public async Task ThePackWiringPutsTheIconIntoThePackage()
    {
        using var evaluation = await EvaluatePackWiringAsync("PackageIcon", TestContext.Current.CancellationToken);

        Assert.Equal("icon.png", evaluation.RootElement.GetProperty("Properties").GetProperty("PackageIcon").GetString());
        Assert.Contains(
            evaluation.RootElement.GetProperty("Items").GetProperty("None").EnumerateArray(),
            item => item.GetProperty("Filename").GetString() == "icon"
                && item.GetProperty("Extension").GetString() == ".png"
                && item.TryGetProperty("Pack", out var pack) && pack.GetString() == "true"
                && item.GetProperty("PackagePath").GetString() == "/");
    }

    [Fact]
    public void ThePackageIconIsAPngWithinTheNuGetSizeLimit()
    {
        var icon = new FileInfo(Path.Combine(FindRepositoryRoot().FullName, "assets", "icon.png"));
        byte[] pngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.True(icon.Exists);
        Assert.InRange(icon.Length, 1, 1024 * 1024);
        Assert.Equal(pngSignature, File.ReadAllBytes(icon.FullName).Take(pngSignature.Length));
    }

    private static async Task<JsonDocument> EvaluatePackWiringAsync(string property, CancellationToken cancellationToken)
    {
        var project = Path.Combine(FindRepositoryRoot().FullName, "src", "Claudalisk.Domain", "Claudalisk.Domain.csproj");
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in new[] { "msbuild", project, $"-getProperty:{property}", "-getItem:None" })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);

        Assert.Equal(0, process.ExitCode);
        return JsonDocument.Parse(output);
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
