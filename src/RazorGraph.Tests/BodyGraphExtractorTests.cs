namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// The graph inside one method. NestedFlow.Tally in the SampleLib fixture is
/// deliberately foreach > if > for > if with one call at the bottom, so both
/// claims are checkable: the nesting metric reads 4, and the body graph anchors
/// the call site at guard depth 4 under the same m: id the main graph uses.
/// </summary>
[Trait("Category", "Integration")]
public class BodyGraphExtractorTests : IAsyncLifetime
{
    private const string TallyId = "m:SampleLib.NestedFlow.Tally(System.Collections.Generic.IEnumerable<int>)";
    private const string SeedId = "m:SampleLib.CatalogStore.Seed()";

    private static readonly SemaphoreSlim LoadGate = new(1, 1);
    private static RoslynExtractor? _roslyn;

    public async Task InitializeAsync()
    {
        await LoadGate.WaitAsync();
        try
        {
            if (_roslyn != null) return;

            var fixtureDir = FixtureDir();
            EnsureRestored(fixtureDir);

            var extractor = new RoslynExtractor();
            await extractor.LoadProjectAsync(Path.Combine(fixtureDir, "SampleLib", "SampleLib.csproj"));
            _roslyn = extractor;   // kept for the test run; the workspace outlives any one test
        }
        finally
        {
            LoadGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void BodyGraph_AnchorsTheDeepCall_AtGuardDepth4()
    {
        var body = _roslyn!.GetMethodBodyGraph(TallyId);

        Assert.NotNull(body);
        Assert.Equal(4, body!.NestingDepth);

        var call = body.Blocks.SelectMany(b => b.Calls).Single(c => c.TargetId == SeedId);
        Assert.Equal(4, call.GuardDepth);
        Assert.True(call.Line > 0);
    }

    [Fact]
    public void BodyGraph_HasEntryExit_AndConditionalBranches()
    {
        var body = _roslyn!.GetMethodBodyGraph(TallyId)!;

        Assert.Contains(body.Blocks, b => b.Kind == "Entry");
        Assert.Contains(body.Blocks, b => b.Kind == "Exit");
        // Two ifs and two loop conditions: conditional branch edges must exist.
        Assert.Contains(body.Blocks, b => b.BranchesTo != null && b.BranchWhen != null);

        // Every edge points at a real block.
        var ordinals = body.Blocks.Select(b => b.Ordinal).ToHashSet();
        Assert.All(body.Blocks, b =>
        {
            if (b.FallsTo is { } f) Assert.Contains(f, ordinals);
            if (b.BranchesTo is { } c) Assert.Contains(c, ordinals);
        });
    }

    [Fact]
    public void BodyGraph_OfUnknownMethod_IsNull()
    {
        Assert.Null(_roslyn!.GetMethodBodyGraph("m:SampleLib.NestedFlow.DoesNotExist()"));
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.sln")))
            dir = dir.Parent;
        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Could not locate RazorGraphTool.sln above the test directory."),
            "tests", "fixtures", "MultiProject");
    }

    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "SampleLib", "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", "restore MultiProject.sln")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit();
    }
}
