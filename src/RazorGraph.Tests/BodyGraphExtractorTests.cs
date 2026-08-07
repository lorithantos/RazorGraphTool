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
        // Two ifs and two loop conditions: conditional blocks must exist.
        Assert.Contains(body.Blocks, b => b.Condition != null && b.WhenTrue != null && b.WhenFalse != null);

        // Every edge points at a real block.
        var ordinals = body.Blocks.Select(b => b.Ordinal).ToHashSet();
        Assert.All(body.Blocks, b =>
        {
            if (b.FallsTo is { } f) Assert.Contains(f, ordinals);
            if (b.WhenTrue is { } t) Assert.Contains(t, ordinals);
            if (b.WhenFalse is { } w) Assert.Contains(w, ordinals);
        });
    }

    [Fact]
    public void BodyGraph_OfUnknownMethod_IsNull()
    {
        Assert.Null(_roslyn!.GetMethodBodyGraph("m:SampleLib.NestedFlow.DoesNotExist()"));
    }

    [Fact]
    public void Compare_MethodWithItself_IsEquivalent()
    {
        var one = _roslyn!.GetMethodBodyGraph(TallyId)!;
        var two = _roslyn!.GetMethodBodyGraph(TallyId)!;

        var diff = BodyGraphComparer.Compare(one, two);

        Assert.True(diff.Equivalent, string.Join(" | ", diff.Differences));
    }

    [Fact]
    public void Compare_NestedAndGuardClauseTwins_ProvesFlowEquivalence()
    {
        // Tally is foreach > if > for > if (depth 4); TallyFlat does the same
        // work with inverted guard clauses (depth 3). Condition canonicalization
        // folds the inversions, so the prover must find identical flow — this is
        // the safety proof for the flattening transform.
        var nested = _roslyn!.GetMethodBodyGraph(TallyId)!;
        var flat = _roslyn!.GetMethodBodyGraph(
            "m:SampleLib.NestedFlow.TallyFlat(System.Collections.Generic.IEnumerable<int>)")!;

        var diff = BodyGraphComparer.Compare(nested, flat);

        Assert.True(diff.Equivalent, string.Join(" | ", diff.Differences));
        Assert.Equal(4, diff.NestingDepthLeft);
        Assert.Equal(3, diff.NestingDepthRight);
    }

    [Fact]
    public void Compare_DifferentMethods_ReportsPreciseDifferences()
    {
        var tally = _roslyn!.GetMethodBodyGraph(TallyId)!;
        var zero = _roslyn!.GetMethodBodyGraph("m:SampleLib.NestedFlow.Zero()")!;

        var diff = BodyGraphComparer.Compare(tally, zero);

        Assert.False(diff.Equivalent);
        Assert.NotEmpty(diff.Differences);
        Assert.Contains(SeedId, diff.CallsOnlyInLeft);
    }

    [Fact]
    public void BodyGraph_MarksThrowAndRethrowBranchSemantics()
    {
        var thrower = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.UnguardedThrow()")!;
        var rethrower = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.RethrowingCaller()")!;

        Assert.Contains(thrower.Blocks, b => b.BranchSemantics == "Throw");
        Assert.Contains(rethrower.Blocks, b => b.BranchSemantics == "Rethrow");
        // A returning block must not read as a throwing one.
        var zero = _roslyn!.GetMethodBodyGraph("m:SampleLib.NestedFlow.Zero()")!;
        Assert.DoesNotContain(zero.Blocks, b => b.BranchSemantics is "Throw" or "Rethrow");
    }

    [Fact]
    public void BodyGraph_CatchRegionsCarryTheirExceptionType()
    {
        var guarded = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.GuardedCaller()")!;
        var rethrowing = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.RethrowingCaller()")!;

        Assert.Contains(guarded.Regions, r => r.ExceptionType == "System.InvalidOperationException");
        // An untyped catch-all carries no type — Object is normalized to null.
        Assert.All(rethrowing.Regions, r => Assert.Null(r.ExceptionType));
    }

    // Pinned against the hole this closed: before ExceptionType reached the
    // comparer, two bodies identical except for what their catch clauses
    // caught compared Equivalent.
    [Fact]
    public void Compare_CatchTypesDiffer_ReportsDifferent()
    {
        var guarded = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.GuardedCaller()")!;
        var misguarded = _roslyn!.GetMethodBodyGraph("m:SampleLib.Throwing.MisguardedCaller()")!;

        var diff = BodyGraphComparer.Compare(guarded, misguarded);

        Assert.False(diff.Equivalent);
        Assert.Contains(diff.Differences, d => d.Contains("exception context"));
    }

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return Path.Combine(
            dir?.FullName ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory."),
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
