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

        // OnGet, OnGetFlaky, OnGetWrapped, OnTick, OnFilteredTick,
        // OnExtensionTick, FireAndForget, CompareItems, RegisterLambda,
        // FaultyMiddleware.InvokeAsync, FaultyHosted.StartAsync — and nothing
        // at OnPost or ShapingMiddleware.
        Assert.Equal(11, escapes.Count);
        Assert.DoesNotContain(escapes, e => e.EntryPoint.Name == "OnPost");
        Assert.Equal(0, escapes[0].Edge.GetProperty<int>("depth")); // shallowest first
    }

    // ---- Extension methods ------------------------------------------------

    // The reduced call form drops the this parameter from the bound symbol;
    // before MethodId unreduced it, every edge into an extension method was
    // silently lost on the id mismatch — and with it, coverage and escapes.
    [Fact]
    public void ExtensionMethods_ReducedCallsBindToTheDeclarationNode()
    {
        var doubleOrThrow = MethodNode("ThrowingExtensions", "DoubleOrThrow");
        var onExtensionTick = MethodNode("Widget", "OnExtensionTick");

        Assert.Contains(_graph!.Outgoing(onExtensionTick.Id),
            e => e.Type == EdgeType.Calls && e.ToId == doubleOrThrow.Id);

        var escape = Assert.Single(IncomingEscapes(onExtensionTick));
        Assert.Equal(2, escape.GetProperty<int>("depth"));
        Assert.Equal(
            new List<string> { UnguardedThrowId, doubleOrThrow.Id, onExtensionTick.Id },
            escape.GetProperty<List<string>>("path"));
    }

    [Fact]
    public void ExtensionMethods_CarryAnExtendsEdgeToTheirType()
    {
        var doubleOrThrow = MethodNode("ThrowingExtensions", "DoubleOrThrow");

        Assert.Equal("SampleLib.Throwing", doubleOrThrow.GetProperty<string>("extendsType"));
        var extends = Assert.Single(
            _graph!.Outgoing(doubleOrThrow.Id), e => e.Type == EdgeType.Extends);
        Assert.Equal("Throwing", _graph!.GetNode(extends.ToId)!.Name);
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
    public void Escapes_MiddlewareIsItsOwnEntryKind()
    {
        var invokeAsync = MethodNode("FaultyMiddleware", "InvokeAsync");
        Assert.Equal("middleware", invokeAsync.GetProperty<string>("entryPointKind"));

        var escape = Assert.Single(IncomingEscapes(invokeAsync));
        Assert.Equal("SampleLib.CustomException", escape.GetProperty<string>("exceptionType"));
    }

    [Fact]
    public void Escapes_FrameworkInterfaceImplementationIsAnEntrySurface()
    {
        var startAsync = MethodNode("FaultyHosted", "StartAsync");
        Assert.Equal("frameworkInterface", startAsync.GetProperty<string>("entryPointKind"));
        Assert.Single(IncomingEscapes(startAsync));

        // The non-throwing sibling is an entry surface with nothing arriving.
        Assert.Empty(IncomingEscapes(MethodNode("FaultyHosted", "StopAsync")));
    }

    // ---- Interface dispatch (the DI default) ------------------------------

    // Callers bind to IFlaky; the throw lives in FlakyService. The chain
    // exists only because the sweep widens implementation facts to the
    // interface's callers — before that, every DI-shaped escape died here.
    [Fact]
    public void Escapes_DispatchThroughAnInterfaceReachesTheHandler()
    {
        var onGetFlaky = MethodNode("FaultyModel", "OnGetFlaky");
        var risky = MethodNode("FlakyService", "Risky");

        var escape = Assert.Single(IncomingEscapes(onGetFlaky));
        Assert.Equal("SampleLib.CustomException", escape.GetProperty<string>("exceptionType"));
        Assert.Equal(2, escape.GetProperty<int>("depth"));
        Assert.Equal(
            new List<string> { UnguardedThrowId, risky.Id, onGetFlaky.Id },
            escape.GetProperty<List<string>>("path"));
    }

    [Fact]
    public void ImplementationsCarryMethodLevelImplementsEdges()
    {
        var risky = MethodNode("FlakyService", "Risky");
        var interfaceRisky = MethodNode("IFlaky", "Risky");

        Assert.Contains(_graph!.Outgoing(risky.Id),
            e => e.Type == EdgeType.Implements && e.ToId == interfaceRisky.Id);
    }

    // ---- Exception boundaries (middleware shaping) ------------------------

    [Fact]
    public void Boundaries_RecordTheirCatchSets()
    {
        var shaping = MethodNode("ShapingMiddleware", "InvokeAsync");

        Assert.Equal("middleware", shaping.GetProperty<string>("entryPointKind"));
        Assert.Equal(
            new List<string> { "SampleLib.CustomException" },
            shaping.GetProperty<List<string>>("boundaryCatches"));
    }

    // A CustomException into an HTTP entry is shaped by design (the 422); an
    // ApplicationException is outside the boundary's catch set and stays a
    // raw failure. Both are reported — disposition, not suppression.
    [Fact]
    public void Escapes_IntoHttpEntries_CarryTheirBoundaryDisposition()
    {
        var shapingId = MethodNode("ShapingMiddleware", "InvokeAsync").Id;

        var shaped = Assert.Single(IncomingEscapes(MethodNode("FaultyModel", "OnGet")));
        Assert.Equal(
            new List<string> { shapingId },
            shaped.GetProperty<List<string>>("interceptedBy"));

        var raw = Assert.Single(IncomingEscapes(MethodNode("FaultyModel", "OnGetWrapped")));
        Assert.Equal("System.ApplicationException", raw.GetProperty<string>("exceptionType"));
        Assert.Null(raw.GetProperty<List<string>>("interceptedBy"));

        // Non-HTTP entries sit on no pipeline; no disposition is claimed.
        var desktop = Assert.Single(IncomingEscapes(MethodNode("Widget", "OnTick")));
        Assert.Null(desktop.GetProperty<List<string>>("interceptedBy"));
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
