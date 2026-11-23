namespace RazorGraph.Tests;

using System.Diagnostics;
using System.Text.Json;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// server_info names the build this process loaded, not the one the 'current' junction names
/// by the time it is asked. A rotation repoints the junction before it stops the old process,
/// and in that window the old process used to report the new build beside its own pid.
/// </summary>
/// <remarks>
/// Real junctions, made with mklink /J in a temp directory, rather than a stand-in for the
/// resolution: following the junction is the very step that had to move from call time to
/// startup, and ResolveLinkTarget's behaviour on a retargeted junction is what is being relied
/// on. Junctions need no elevation, unlike symbolic links.
/// </remarks>
public sealed class BuildLocationJunctionTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "rg-buildlocation-" + Guid.NewGuid().ToString("N"));
    private readonly string buildA;
    private readonly string buildB;
    private readonly string current;

    public BuildLocationJunctionTests()
    {
        buildA = Directory.CreateDirectory(Path.Combine(root, "build-A")).FullName;
        buildB = Directory.CreateDirectory(Path.Combine(root, "build-B")).FullName;
        current = Path.Combine(root, "current");
    }

    [Fact]
    public void ServerStartedThroughTheJunction_KeepsReportingItsBuild_AfterTheJunctionMoves()
    {
        Junction(current, buildA);
        var location = new BuildLocation(current + Path.DirectorySeparatorChar);

        using (var before = Info(location))
        {
            AssertSamePath(buildA, before.RootElement.GetProperty("build").GetString());
            Assert.False(before.RootElement.TryGetProperty("currentNow", out _));
        }

        // The rotation's move: same link, new target, old process still answering.
        Directory.Delete(current);
        Junction(current, buildB);

        using var after = Info(location);
        AssertSamePath(buildA, after.RootElement.GetProperty("build").GetString());
        AssertSamePath(buildB, after.RootElement.GetProperty("currentNow").GetString());
    }

    [Fact]
    public void ServerStartedFromAPlainDirectory_ReportsThatDirectory_AndNoCurrentNow()
    {
        using var info = Info(new BuildLocation(buildA));

        AssertSamePath(buildA, info.RootElement.GetProperty("build").GetString());
        Assert.False(info.RootElement.TryGetProperty("currentNow", out _));
    }

    public void Dispose()
    {
        // The link first, so nothing can follow it into a target.
        if (Directory.Exists(current)) Directory.Delete(current);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }

    private static JsonDocument Info(BuildLocation location) =>
        JsonDocument.Parse(new ServerTools(new GraphStore()).ServerInfo(null, location));

    private static void AssertSamePath(string expected, string? actual) =>
        Assert.Equal(
            Path.TrimEndingDirectorySeparator(expected),
            Path.TrimEndingDirectorySeparator(actual ?? ""),
            ignoreCase: true);

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in (string[])["/c", "mklink", "/J", link, target]) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(Task.WhenAll(stdout, stderr, process.WaitForExitAsync()).Wait(TimeSpan.FromSeconds(10)), "mklink did not finish");
        Assert.True(process.ExitCode == 0, $"mklink /J failed: {stderr.Result}{stdout.Result}");
    }
}
