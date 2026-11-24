namespace RazorGraph.Tests;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// Every successful build is saved to the server's cache, so a session that
/// restarts can load_graph it instead of compiling again. The cache directory is
/// pointed at a scratch folder here, so a run neither reads nor litters the real
/// per-user one.
/// </summary>
public sealed class GraphCacheTests : IDisposable
{
    private readonly string _root =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-cache-{Guid.NewGuid():N}")).FullName;

    private string Cache => Path.Combine(_root, "cache");

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildSolution_CachesTheGraph_AsIdDashSourceHash_AndLoadGraphRestoresIt()
    {
        var solution = Path.Combine(FixtureDir(), "MultiProject.sln");
        EnsureRestored(FixtureDir());

        using var built = JsonDocument.Parse(
            await new GraphStoreTools(new GraphStore()) { CacheDirectory = Cache }
                .BuildSolution(solution, graphId: "multi", excludeText: true));
        var savedTo = built.RootElement.GetProperty("savedTo").GetString()!;

        Assert.False(built.RootElement.TryGetProperty("saveError", out _));
        Assert.Equal(Path.GetFullPath(Cache), Path.GetDirectoryName(savedTo), ignoreCase: true);
        Assert.Equal($"multi-{Hash8(solution)}.json", Path.GetFileName(savedTo));

        // A later session: a fresh server with nothing in it but the file.
        using var loaded = JsonDocument.Parse(await new GraphStoreTools(new GraphStore()).LoadGraph(savedTo, "restored"));
        Assert.Equal(built.RootElement.GetProperty("nodes").GetInt32(), loaded.RootElement.GetProperty("nodes").GetInt32());
        Assert.Equal(built.RootElement.GetProperty("edges").GetInt32(), loaded.RootElement.GetProperty("edges").GetInt32());
        Assert.Equal(solution, loaded.RootElement.GetProperty("source").GetString(), ignoreCase: true);
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task BuildGraph_OfTheSameSourceTwice_OverwritesOneFile()
    {
        var project = Path.Combine(FixtureDir(), "SampleLib", "SampleLib.csproj");
        EnsureRestored(FixtureDir());
        var tools = new GraphStoreTools(new GraphStore()) { CacheDirectory = Cache };

        var first = SavedTo(await tools.BuildGraph(project, graphId: "lib", excludeText: true));
        var second = SavedTo(await tools.BuildGraph(project, graphId: "lib", excludeText: true));

        Assert.Equal(first, second);
        Assert.Equal([first], Directory.GetFiles(Cache));
    }

    [Fact]
    public void UnwritableCache_ReportsSaveError_AndTheBuildStillSucceeds()
    {
        // A directory cannot be created under a file, on any platform.
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "");
        var store = new GraphStore();
        var tools = new GraphStoreTools(store) { CacheDirectory = Path.Combine(blocker, "cache") };

        using var doc = JsonDocument.Parse(tools.BuildLua(LuaTree("tree"), graphId: "lua"));

        var error = doc.RootElement.GetProperty("saveError").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.DoesNotContain('\n', error);
        Assert.False(doc.RootElement.TryGetProperty("savedTo", out _));
        Assert.True(doc.RootElement.GetProperty("nodes").GetInt32() > 0);
        Assert.Same(store.Require("lua").Graph, store.Require(null).Graph);
    }

    [Fact]
    public void SameGraphId_FromTwoSources_IsTwoFiles()
    {
        var tools = new GraphStoreTools(new GraphStore()) { CacheDirectory = Cache };

        var one = SavedTo(tools.BuildLua(LuaTree("one"), graphId: "lua"));
        var two = SavedTo(tools.BuildLua(LuaTree("two"), graphId: "lua"));

        Assert.NotEqual(one, two);
        Assert.Equal(2, Directory.GetFiles(Cache).Length);
        Assert.True(File.Exists(one));
        Assert.True(File.Exists(two));
    }

    /// <summary>
    /// Computed here rather than asked of the code under test, so a name that drops
    /// or changes the hash fails rather than agreeing with itself.
    /// </summary>
    private static string Hash8(string source)
    {
        var normalised = Path.GetFullPath(source);
        if (OperatingSystem.IsWindows()) normalised = normalised.ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalised)))[..8].ToLowerInvariant();
    }

    private static string SavedTo(string response)
    {
        using var doc = JsonDocument.Parse(response);
        return doc.RootElement.GetProperty("savedTo").GetString()!;
    }

    private string LuaTree(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "main.lua"), "local M = {}\nfunction M.hello() return 1 end\nreturn M\n");
        return dir;
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    private static string FixtureDir() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "MultiProject");

    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "SampleLib", "obj", "project.assets.json"))) return;
        BoundedWaits.Restore(fixtureDir, "MultiProject.sln", "the fixture solution");
    }
}
