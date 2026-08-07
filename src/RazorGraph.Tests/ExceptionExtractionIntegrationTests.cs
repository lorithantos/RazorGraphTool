namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// The extraction half of exception-escape analysis, over the SampleLib
/// Throwing fixture: which types a method's own throws let escape, and which
/// catch guards each call edge carries. The sweep that turns these facts into
/// Escapes edges has its own tests; these pin the facts themselves.
/// </summary>
[Trait("Category", "Integration")]
public class ExceptionExtractionIntegrationTests : IAsyncLifetime
{
    private const string UnguardedThrowId = "m:SampleLib.Throwing.UnguardedThrow()";

    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _graph;

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_graph != null) return;

            var fixtureDir = FixtureDir();
            EnsureRestored(fixtureDir);

            await using var builder = new GraphBuilder();
            _graph = await builder.BuildFromSolutionAllAsync(
                Path.Combine(fixtureDir, "MultiProject.sln"));
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public void Throws_StampsLocallyUnhandledTypes()
    {
        Assert.Equal(
            new List<string> { "SampleLib.CustomException" },
            _graph!.GetNode(UnguardedThrowId)!.GetProperty<List<string>>("throws"));

        // The wrapper's own throw is the wrapper type, never the wrapped one.
        Assert.Equal(
            new List<string> { "System.ApplicationException" },
            _graph!.GetNode("m:SampleLib.Throwing.WrappingCaller()")!.GetProperty<List<string>>("throws"));

        // A bare rethrow out of an untyped catch is System.Exception — the honest upper bound.
        Assert.Equal(
            new List<string> { "System.Exception" },
            _graph!.GetNode("m:SampleLib.Throwing.RethrowingCaller()")!.GetProperty<List<string>>("throws"));
    }

    [Fact]
    public void Throws_AbsentWhenHandledInTheSameMethod()
    {
        Assert.Null(_graph!.GetNode("m:SampleLib.Throwing.SafeThrow()")!.GetProperty<List<string>>("throws"));
        // Guarded callers throw nothing of their own; their guards live on the call edge.
        Assert.Null(_graph!.GetNode("m:SampleLib.Throwing.GuardedCaller()")!.GetProperty<List<string>>("throws"));
    }

    [Fact]
    public void CallEdges_CarryTheirCatchGuards()
    {
        Assert.Equal(
            new List<string> { "System.InvalidOperationException" },
            EdgeTo("m:SampleLib.Throwing.GuardedCaller()").GetProperty<List<string>>("guardedBy"));

        // The guard is recorded as declared; whether it matches what arrives
        // is the sweep's assignability decision, not extraction's.
        Assert.Equal(
            new List<string> { "System.ArgumentException" },
            EdgeTo("m:SampleLib.Throwing.MisguardedCaller()").GetProperty<List<string>>("guardedBy"));

        // An untyped catch-all is the "*" sentinel.
        Assert.Equal(
            new List<string> { "*" },
            EdgeTo("m:SampleLib.Throwing.RethrowingCaller()").GetProperty<List<string>>("guardedBy"));
    }

    [Fact]
    public void CallEdges_KeepFilteredCatchesApartFromFirmOnes()
    {
        var filtered = EdgeTo("m:SampleLib.Throwing.FilteredCaller()");

        Assert.Null(filtered.GetProperty<List<string>>("guardedBy"));
        Assert.Equal(
            new List<string> { "SampleLib.CustomException" },
            filtered.GetProperty<List<string>>("filteredBy"));
    }

    [Fact]
    public void CallEdges_OutsideAnyTry_CarryNoGuards()
    {
        var call = _graph!.Outgoing("m:SampleLib.NestedFlow.Tally(System.Collections.Generic.IEnumerable<int>)")
            .Single(e => e.Type == EdgeType.Calls);

        Assert.Null(call.GetProperty<List<string>>("guardedBy"));
        Assert.Null(call.GetProperty<List<string>>("filteredBy"));
    }

    // ---- The sweep: Escapes edges -----------------------------------------

    [Fact]
    public void Escapes_CrossProjectThrowReachesThePageHandler()
    {
        var onGet = MethodNode("FaultyModel", "OnGet");
        Assert.Equal("pageHandler", onGet.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(onGet));
        Assert.Equal(UnguardedThrowId, escape.FromId);
        Assert.Equal("SampleLib.CustomException", escape.GetProperty<string>("exceptionType"));
        Assert.Equal(1, escape.GetProperty<int>("depth"));
        Assert.Equal(new List<string> { UnguardedThrowId, onGet.Id }, escape.GetProperty<List<string>>("path"));
        Assert.False(escape.GetProperty<bool>("conditional"));
    }

    [Fact]
    public void Escapes_NothingReachesTheGuardedHandler()
    {
        Assert.Empty(IncomingEscapes(MethodNode("FaultyModel", "OnPost")));
    }

    [Fact]
    public void Escapes_FilterOnlyHandlingArrivesConditional()
    {
        var onFilteredTick = MethodNode("Widget", "OnFilteredTick");
        Assert.Equal("eventHandler", onFilteredTick.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(onFilteredTick));
        Assert.True(escape.GetProperty<bool>("conditional"));
        Assert.Equal(2, escape.GetProperty<int>("depth"));
        Assert.Equal(UnguardedThrowId, escape.GetProperty<List<string>>("path")![0]);
    }

    [Fact]
    public void Escapes_EntryPointThrowingDirectlyIsADepthZeroSelfEscape()
    {
        var fireAndForget = MethodNode("Widget", "FireAndForget");
        Assert.Equal("asyncVoid", fireAndForget.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(fireAndForget));
        Assert.Equal(fireAndForget.Id, escape.FromId);
        Assert.Equal("System.InvalidOperationException", escape.GetProperty<string>("exceptionType"));
        Assert.Equal(0, escape.GetProperty<int>("depth"));
    }

    [Fact]
    public void Escapes_QuerySurfacesTheFixtureChains()
    {
        var escapes = new RazorGraph.Core.Query.GraphQuery(_graph!)
            .FindEscapingExceptions(project: "SampleWeb")
            .ToList();

        // OnGet, OnTick, OnFilteredTick, FireAndForget, CompareItems,
        // RegisterLambda, InvokeAsync — and nothing at OnPost.
        Assert.Equal(7, escapes.Count);
        Assert.DoesNotContain(escapes, e => e.EntryPoint.Name == "OnPost");
        Assert.Equal(0, escapes[0].Edge.GetProperty<int>("depth")); // shallowest first
    }

    // ---- Delegates and invisible registration -----------------------------

    [Fact]
    public void Escapes_MethodGroupHandedToTheFrameworkIsACallbackSurface()
    {
        var compareItems = MethodNode("CallbackHost", "CompareItems");
        Assert.Equal("callback", compareItems.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(compareItems));
        Assert.Equal(UnguardedThrowId, escape.FromId);
        Assert.Equal(1, escape.GetProperty<int>("depth"));

        // The registration itself is an unguarded delegate edge.
        var edge = _graph!.Outgoing(MethodNode("CallbackHost", "RegisterComparer").Id)
            .Single(e => e.Type == EdgeType.Calls && e.ToId == compareItems.Id);
        Assert.True(edge.GetProperty<bool>("viaDelegate"));
        Assert.Null(edge.GetProperty<List<string>>("guardedBy"));
    }

    [Fact]
    public void Escapes_DelegateKeptInSolutionConnectsButIsNoEntryPoint()
    {
        var format = MethodNode("CallbackHost", "Format");

        Assert.Null(format.GetProperty<string>("entryPointKind"));
        Assert.True(_graph!.Outgoing(MethodNode("CallbackHost", "KeepFormatter").Id)
            .Single(e => e.Type == EdgeType.Calls && e.ToId == format.Id)
            .GetProperty<bool>("viaDelegate"));
    }

    [Fact]
    public void Escapes_LambdaHandedToTheFrameworkMarksItsContainer()
    {
        var registerLambda = MethodNode("CallbackHost", "RegisterLambda");
        Assert.Equal("callback", registerLambda.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(registerLambda));
        Assert.Equal(UnguardedThrowId, escape.FromId);
    }

    [Fact]
    public void Escapes_FrameworkInterfaceImplementationIsAnEntrySurface()
    {
        var invokeAsync = MethodNode("FaultyMiddleware", "InvokeAsync");
        Assert.Equal("frameworkInterface", invokeAsync.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(invokeAsync));
        Assert.Equal("SampleLib.CustomException", escape.GetProperty<string>("exceptionType"));
    }

    [Fact]
    public void Throws_InsideALambdaAttributeToTheContainerAsConditional()
    {
        var holder = MethodNode("CallbackHost", "HoldThrowingLambda");

        Assert.Equal(
            new List<string> { "SampleLib.CustomException" },
            holder.GetProperty<List<string>>("throws"));
        // In-solution registration: connected facts, but no entry surface.
        Assert.Null(holder.GetProperty<string>("entryPointKind"));
        Assert.Empty(IncomingEscapes(holder));
    }

    private static GraphNode MethodNode(string declaringTypeName, string methodName) =>
        _graph!.NodesOfType(NodeType.Method).Single(n =>
            n.Name == methodName
            && (n.GetProperty<string>("declaringType") ?? "").EndsWith("." + declaringTypeName, StringComparison.Ordinal));

    private static List<GraphEdge> IncomingEscapes(GraphNode entry) =>
        _graph!.Incoming(entry.Id).Where(e => e.Type == EdgeType.Escapes).ToList();

    private static GraphEdge EdgeTo(string callerId) =>
        _graph!.Outgoing(callerId).Single(e => e.Type == EdgeType.Calls && e.ToId == UnguardedThrowId);

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
