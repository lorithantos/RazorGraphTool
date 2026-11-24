namespace RazorGraph.Tests;

using System.Text.Json;
using ModelContextProtocol;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// A build never restores, so a project cloned and graphed before dotnet restore
/// has no project.assets.json: its packages do not resolve, its [Fact] does not
/// bind, and the graph holds no test methods. These pin that the build names the
/// project with the fix, and that the coverage refusal names it as the cause.
///
/// The MultiProject fixture is copied to a temp directory without its bin and obj,
/// restored there, and then the test project's assets file is deleted: the shared
/// fixture's own assets files are never touched, since every other build needs them.
/// </summary>
[Trait("Category", "Integration")]
public sealed class UnrestoredProjectTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rg-unrestored-" + Guid.NewGuid().ToString("N"));
    private readonly GraphStore _store = new();
    private string _solutionPath = "";
    private string _response = "";
    private CodeGraph? _artifactsGraph;
    private IReadOnlyList<string> _artifactsFailures = [];

    public async Task InitializeAsync()
    {
        var fixture = Path.Combine(RepoRoot(), "tests", "fixtures", "MultiProject");
        var copy = Path.Combine(_dir, "MultiProject");
        CopyWithoutBuildOutput(fixture, copy);
        _solutionPath = Path.Combine(copy, "MultiProject.sln");
        Restore(copy, "MultiProject.sln");
        File.Delete(Path.Combine(copy, "SampleWeb.Tests", "obj", "project.assets.json"));

        // Cached inside the temp directory, so the run neither litters nor reads the
        // real per-user cache.
        var tools = new GraphStoreTools(_store) { CacheDirectory = Path.Combine(_dir, "cache") };
        _response = await BoundedWaits.WithinAsync(
            ct => tools.BuildSolution(_solutionPath, graphId: "unrestored", excludeText: true, ct: ct),
            BoundedWaits.BuildTimeout, "build_solution of the unrestored MultiProject copy");

        // The false-alarm case: a restored project whose obj is not beside it. The
        // artifacts layout restores to artifacts\obj\<project>\, and a check that
        // only looked in .\obj would call it unrestored.
        var arty = Path.Combine(_dir, "Arty");
        Write(Path.Combine(arty, "Directory.Build.props"), """
            <Project>
              <PropertyGroup>
                <UseArtifactsOutput>true</UseArtifactsOutput>
              </PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(arty, "Arty", "Arty.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        Write(Path.Combine(arty, "Arty", "Widget.cs"), "namespace Arty; public class Widget { }\n");
        Restore(Path.Combine(arty, "Arty"), "Arty.csproj");
        Assert.False(Directory.Exists(Path.Combine(arty, "Arty", "obj")), "the artifacts layout did not move obj");

        await using var builder = new GraphBuilder { ExcludeText = true };
        _artifactsGraph = await BoundedWaits.WithinAsync(
            ct => builder.BuildFromProjectAsync(Path.Combine(arty, "Arty", "Arty.csproj"), ct),
            BoundedWaits.BuildTimeout, "the build of the artifacts-layout project");
        _artifactsFailures = builder.ProjectFailures;
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    private static void CopyWithoutBuildOutput(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var sub in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(sub);
            if (name is "bin" or "obj") continue;
            CopyWithoutBuildOutput(sub, Path.Combine(to, name));
        }
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private static void Restore(string workingDirectory, string target) =>
        BoundedWaits.Restore(workingDirectory, target, target);

    private List<string> Failures()
    {
        using var doc = JsonDocument.Parse(_response);
        return doc.RootElement.GetProperty("projectFailures").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public void BuildResponse_NamesTheUnrestoredProject_WithTheRestoreThatFixesIt()
    {
        var header = Assert.Single(Failures(), f => f.StartsWith("SampleWeb.Tests: not restored", StringComparison.Ordinal));

        Assert.Contains("no project.assets.json", header);
        Assert.Contains($"run dotnet restore \"{_solutionPath}\"", header);
        // Only the project that lost its assets file: the restored ones stay quiet.
        Assert.DoesNotContain(Failures(), f => f.Contains("not restored") && !f.StartsWith("SampleWeb.Tests: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Graph_RecordsTheUnrestoredProject()
    {
        Assert.Equal(new[] { "SampleWeb.Tests" }, _store.Require("unrestored").Graph.UnrestoredProjects);
    }

    [Fact]
    public void CoveringTests_OnTheUnrestoredGraph_NamesTheCauseAndTheProject()
    {
        var tools = new CoverageTools(_store);

        var refusal = Assert.Throws<McpException>(() =>
            tools.CoveringTests("m:SampleLib.Greeter.Greet(string)", graphId: "unrestored"));

        Assert.Contains("SampleWeb.Tests had no project.assets.json", refusal.Message);
        Assert.Contains($"dotnet restore \"{_solutionPath}\"", refusal.Message);
        Assert.DoesNotContain("built from a single project", refusal.Message);

        // The other two refuse on the same guard, and say the same.
        Assert.Contains("SampleWeb.Tests had no project.assets.json",
            Assert.Throws<McpException>(() => tools.UncoveredMethods("SampleLib", graphId: "unrestored")).Message);
        Assert.Contains("SampleWeb.Tests had no project.assets.json",
            Assert.Throws<McpException>(() => tools.CoveredMethods(
                "m:SampleWeb.Tests.GreeterTests.Greet_ThroughTheInterface()", graphId: "unrestored")).Message);
    }

    [Fact]
    public void ArtifactsLayout_IsRestored_NotAFalseAlarm()
    {
        Assert.NotNull(_artifactsGraph!.UnrestoredProjects);
        Assert.Empty(_artifactsGraph.UnrestoredProjects!);
        Assert.Empty(_artifactsFailures);
    }
}
