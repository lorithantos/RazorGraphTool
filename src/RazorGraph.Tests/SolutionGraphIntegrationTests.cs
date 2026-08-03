namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// End-to-end over a three-project fixture solution: a class library, a Razor web
/// app that consumes it, and a test project that exercises it.
///
/// The point of these tests is the boundary. Every assertion here is about an
/// edge whose two ends live in different projects — the class of fact a graph
/// built one project at a time cannot represent at all, however many times it is
/// rebuilt.
/// </summary>
[Trait("Category", "Integration")]
public class SolutionGraphIntegrationTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _solutionGraph;
    private static CodeGraph? _webOnlyGraph;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.sln")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.sln above the test directory.");
    }

    private static string FixtureDir() =>
        Path.Combine(RepoRoot(), "tests", "fixtures", "MultiProject");

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_solutionGraph != null) return;

            var fixtureDir = FixtureDir();
            var solutionPath = Path.Combine(fixtureDir, "MultiProject.sln");
            Assert.True(File.Exists(solutionPath), $"Fixture solution missing: {solutionPath}");

            EnsureRestored(fixtureDir);

            await using (var builder = new GraphBuilder())
            {
                _solutionGraph = await builder.BuildFromSolutionAllAsync(solutionPath);
            }

            // The contrast case: the same web project on its own.
            await using (var builder = new GraphBuilder())
            {
                _webOnlyGraph = await builder.BuildFromProjectAsync(
                    Path.Combine(fixtureDir, "SampleWeb", "SampleWeb.csproj"));
            }
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

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
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of fixture solution failed:\n{output}");
    }

    private static GraphNode Method(CodeGraph graph, string declaringType, string name) =>
        graph.Nodes.Single(n =>
            n.Type == NodeType.Method &&
            n.Name == name &&
            n.GetProperty<string>("declaringType") == declaringType);

    // ---- Projects ------------------------------------------------------------

    [Fact]
    public void Builds_ProjectNodes_ForEveryProjectInTheSolution()
    {
        var projects = _solutionGraph!.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();

        Assert.Equal(new[] { "SampleLib", "SampleWeb", "SampleWeb.Tests" }, projects);
    }

    [Fact]
    public void Builds_DependsOnEdges_BetweenProjects()
    {
        var edges = _solutionGraph!.Edges
            .Where(e => e.Type == EdgeType.DependsOn)
            .Select(e => (From: _solutionGraph.GetNode(e.FromId)!.Name, To: _solutionGraph.GetNode(e.ToId)!.Name))
            .ToList();

        Assert.Contains(("SampleWeb", "SampleLib"), edges);
        Assert.Contains(("SampleWeb.Tests", "SampleLib"), edges);
    }

    [Fact]
    public void EveryCodeNode_IsAttributedToItsProject()
    {
        var methods = _solutionGraph!.NodesOfType(NodeType.Method).ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.False(
            string.IsNullOrWhiteSpace(m.GetProperty<string>("project")),
            $"{m.Id} has no project attribution"));
    }

    // ---- The cross-project edges that motivated all of this -----------------

    [Fact]
    public void Builds_CallEdge_FromWebProjectIntoClassLibrary()
    {
        var onGet = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet");

        var callees = _solutionGraph!.Outgoing(onGet.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _solutionGraph.GetNode(e.ToId)!)
            .ToList();

        var crossProject = callees.Where(c => c.GetProperty<string>("project") == "SampleLib").ToList();

        Assert.NotEmpty(crossProject);
        Assert.Contains(crossProject, c => c.Name == "List");
    }

    [Fact]
    public void SingleProjectBuild_CannotSeeTheCrossProjectCall()
    {
        // The control for the test above. OnGet still exists, and still calls
        // into SampleLib in the source — but with only one assembly compiled,
        // the callee was never a node, so no edge could be recorded.
        var onGet = Method(_webOnlyGraph!, "SampleWeb.Pages.IndexModel", "OnGet");

        var callees = _webOnlyGraph!.Outgoing(onGet.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _webOnlyGraph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.DoesNotContain("List", callees);
    }

    [Fact]
    public void Builds_InjectedIntoEdge_AcrossProjects()
    {
        var pageModel = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");

        var injectors = _solutionGraph.Incoming(pageModel.Id)
            .Where(e => e.Type == EdgeType.InjectedInto)
            .Select(e => _solutionGraph.GetNode(e.FromId)!)
            .ToList();

        Assert.Contains(injectors, i => i.Name == "ICatalogStore" && i.GetProperty<string>("project") == "SampleLib");
    }

    // ---- Coverage ------------------------------------------------------------

    [Fact]
    public void Marks_TestMethods_ByAttribute()
    {
        var tests = _solutionGraph!.NodesOfType(NodeType.Method)
            .Where(m => m.GetProperty<bool>("isTest"))
            .Select(m => m.Name)
            .ToList();

        Assert.Equal(
            new[] { "Cache_SeedsItself", "List_ReturnsSortedCatalogs", "Preload_CountsCatalogs", "Warm_IsPositive" },
            tests.OrderBy(t => t, StringComparer.Ordinal));
        // NotATest calls production code but carries no attribute, and
        // InitializeAsync is a lifecycle hook, not a test.
        Assert.DoesNotContain("NotATest", tests);
        Assert.DoesNotContain("InitializeAsync", tests);
    }

    [Fact]
    public void Builds_CoversEdges_FromTestToCodeUnderTest()
    {
        var query = new GraphQuery(_solutionGraph!);
        var load = Method(_solutionGraph!, "SampleLib.CatalogStore", "List");

        var covering = query.GetCoveringTests(load.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("List_ReturnsSortedCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void CoversEdges_ReachTransitivelyAndRecordDepth()
    {
        var query = new GraphQuery(_solutionGraph!);
        var normalize = Method(_solutionGraph!, "SampleLib.CatalogStore", "Normalize");

        var covering = query.GetCoveringTests(normalize.Id).ToList();

        // Normalize is private and only reachable through List, so a direct-call
        // model would report it as untested.
        Assert.Single(covering);
        Assert.Equal(2, covering[0].Depth);
    }

    [Fact]
    public void CoversEdges_AreNotEmittedForNonTestCallers()
    {
        var orphan = Method(_solutionGraph!, "SampleLib.CatalogStore", "Orphan");

        // Orphan is called only by NotATest, which has no test attribute.
        Assert.DoesNotContain(_solutionGraph!.Incoming(orphan.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void CoversEdges_FlowThroughLifecycleSetup()
    {
        var query = new GraphQuery(_solutionGraph!);
        var preload = Method(_solutionGraph!, "SampleLib.CatalogStore", "Preload");

        // Preload is called only from LifecycleCatalogTests.InitializeAsync,
        // which xUnit runs around each test — no test method calls it. Before
        // lifecycle seeding this reported Preload as uncovered while runtime
        // coverage showed it exercised.
        var covering = query.GetCoveringTests(preload.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Preload_CountsCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void LifecycleHooks_AreFlagged_ButAreNotTests()
    {
        var initialize = Method(_solutionGraph!, "SampleWeb.Tests.LifecycleCatalogTests", "InitializeAsync");

        Assert.True(initialize.GetProperty<bool>("isTestLifecycle"));
        Assert.False(initialize.GetProperty<bool>("isTest"));
        // The hook itself must never be the source of a Covers edge.
        Assert.DoesNotContain(_solutionGraph!.Outgoing(initialize.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void Dispose_OnProductionTypes_IsNotFlaggedAsLifecycle()
    {
        // CatalogStore is IDisposable but has no tests on the type, so its
        // Dispose must not be flagged — the gate that keeps every production
        // Dispose out of the lifecycle set.
        var dispose = Method(_solutionGraph!, "SampleLib.CatalogStore", "Dispose");

        Assert.False(dispose.GetProperty<bool>("isTestLifecycle"));
    }

    [Fact]
    public void CoversEdges_FlowThroughCtorSetup()
    {
        var warm = Method(_solutionGraph!, "SampleLib.CatalogStore", "Warm");

        // Warm is called only from CtorSetupCatalogTests' constructor, which
        // xUnit runs before every test — no test method calls it.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(warm.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Warm_IsPositive", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void TestClassCtor_IsLifecycle_AndNeverCovered()
    {
        var ctor = Method(_solutionGraph!, "SampleWeb.Tests.CtorSetupCatalogTests", ".ctor");

        Assert.True(ctor.GetProperty<bool>("isTestLifecycle"));
        Assert.False(ctor.GetProperty<bool>("isTest"));
        Assert.DoesNotContain(_solutionGraph!.Incoming(ctor.Id), e => e.Type == EdgeType.Covers);
    }

    [Fact]
    public void ProductionCtor_IsCovered_AndSeedsItsCallees()
    {
        var query = new GraphQuery(_solutionGraph!);

        // InitializeAsync news up CatalogSession: the new-expression is a call
        // edge to the ctor, and Open is only reachable through it.
        var ctor = Method(_solutionGraph!, "SampleLib.CatalogSession", ".ctor");
        var ctorCovering = query.GetCoveringTests(ctor.Id).ToList();
        Assert.Single(ctorCovering);
        Assert.Equal("Preload_CountsCatalogs", ctorCovering[0].Test.Name);
        Assert.Equal(1, ctorCovering[0].Depth);

        var open = Method(_solutionGraph!, "SampleLib.CatalogSession", "Open");
        var openCovering = query.GetCoveringTests(open.Id).ToList();
        Assert.Single(openCovering);
        Assert.Equal(2, openCovering[0].Depth);
    }

    [Fact]
    public void ImplicitCtor_WithInitializers_IsANode_AndRunsThem()
    {
        var query = new GraphQuery(_solutionGraph!);

        // CatalogCache declares no ctor; its implicit ctor is a node because
        // the field initializer is the code it runs. Reaching Seed proves the
        // initializer's call was attributed to that ctor.
        var ctor = Method(_solutionGraph!, "SampleLib.CatalogCache", ".ctor");
        var ctorCovering = query.GetCoveringTests(ctor.Id).ToList();
        Assert.Single(ctorCovering);
        Assert.Equal("Cache_SeedsItself", ctorCovering[0].Test.Name);
        Assert.Equal(1, ctorCovering[0].Depth);

        var seed = Method(_solutionGraph!, "SampleLib.CatalogStore", "Seed");
        var seedCovering = query.GetCoveringTests(seed.Id).ToList();
        Assert.Single(seedCovering);
        Assert.Equal(2, seedCovering[0].Depth);
    }

    [Fact]
    public void ImplicitCtor_WithoutInitializers_IsNotANode()
    {
        // CatalogStore has no explicit ctor and no initializers: its implicit
        // ctor runs nothing, so a node for it would only ever read as noise.
        Assert.DoesNotContain(_solutionGraph!.Nodes, n =>
            n.Type == NodeType.Method &&
            n.Name == ".ctor" &&
            n.GetProperty<string>("declaringType") == "SampleLib.CatalogStore");
    }

    [Fact]
    public void CoversEdges_IncludeUsingDisposal()
    {
        var dispose = Method(_solutionGraph!, "SampleLib.CatalogStore", "Dispose");

        // The test body has `using var store = ...` and never calls Dispose
        // explicitly; the implicit disposal must still count as exercise.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(dispose.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("List_ReturnsSortedCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void CoversEdges_IncludeAwaitUsingDisposal_ThroughLifecycleSetup()
    {
        var disposeAsync = Method(_solutionGraph!, "SampleLib.CatalogSession", "DisposeAsync");

        // The compounding case: DisposeAsync is reached only via an await using
        // inside InitializeAsync — implicit call and lifecycle seeding together.
        var covering = new GraphQuery(_solutionGraph!).GetCoveringTests(disposeAsync.Id).ToList();

        Assert.Single(covering);
        Assert.Equal("Preload_CountsCatalogs", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
    }

    [Fact]
    public void FindUncoveredMethods_ReportsTheUnreachedMethod()
    {
        var uncovered = new GraphQuery(_solutionGraph!)
            .FindUncoveredMethods("SampleLib")
            .Select(m => m.Id)
            .ToList();

        Assert.Contains(Method(_solutionGraph!, "SampleLib.CatalogStore", "Orphan").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "List").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "Normalize").Id, uncovered);
        Assert.DoesNotContain(Method(_solutionGraph!, "SampleLib.CatalogStore", "Preload").Id, uncovered);
    }

    [Fact]
    public void FindUncoveredMethods_ExcludesInterfaceDeclarations()
    {
        // ICatalogStore.List has no body, so it can never carry a Covers edge.
        // Reporting it as untested would be noise that discredits the report.
        var declaration = Method(_solutionGraph!, "SampleLib.ICatalogStore", "List");
        Assert.True(declaration.GetProperty<bool>("isAbstract"));

        var uncovered = new GraphQuery(_solutionGraph!)
            .FindUncoveredMethods("SampleLib")
            .Select(m => m.Id)
            .ToList();

        Assert.DoesNotContain(declaration.Id, uncovered);
    }

    // ---- Id scoping ----------------------------------------------------------

    [Fact]
    public void RazorAndAssetIds_AreScopedByProject()
    {
        var page = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        Assert.StartsWith("page:SampleWeb/", page.Id);
        Assert.Equal("SampleWeb", page.GetProperty<string>("project"));

        var siteJs = _solutionGraph.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");
        Assert.Equal("js:SampleWeb/wwwroot/js/site.js", siteJs.Id);
    }

    [Fact]
    public void SingleProjectBuild_KeepsUnscopedIds()
    {
        // Existing saved graphs and their ids must survive this change.
        var page = _webOnlyGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        Assert.StartsWith("page:Pages", page.Id);
        Assert.Contains(_webOnlyGraph.Nodes, n => n.Id == "js:wwwroot/js/site.js");
    }

    // ---- Inline scripts ------------------------------------------------------

    [Fact]
    public void Builds_InlineScriptNode_AlongsideTheExternalFile()
    {
        var scripts = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).ToList();

        var inline = scripts.SingleOrDefault(s => s.GetProperty<bool>("inline"));
        Assert.NotNull(inline);
        Assert.Contains("#inline-", inline!.Id);
        Assert.NotNull(inline.LineStart);

        // The external file is still its own node, not absorbed into the page.
        Assert.Contains(scripts, s => s.Name == "site.js" && !s.GetProperty<bool>("inline"));
    }

    [Fact]
    public void InlineScript_IsWiredToItsPage()
    {
        var inline = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).Single(s => s.GetProperty<bool>("inline"));

        var referencedBy = _solutionGraph.Incoming(inline.Id)
            .Where(e => e.Type == EdgeType.References)
            .Select(e => _solutionGraph.GetNode(e.FromId)!.Type)
            .ToList();

        Assert.Contains(NodeType.RazorPage, referencedBy);
    }

    [Fact]
    public void InlineScript_ParticipatesInServerToClientCoupling()
    {
        var inline = _solutionGraph!.NodesOfType(NodeType.JavaScriptFile).Single(s => s.GetProperty<bool>("inline"));

        var coupling = _solutionGraph.Incoming(inline.Id).SingleOrDefault(e => e.Type == EdgeType.ViewDataReadBy);

        // data-catalog-count is rendered from server state and read by the inline
        // block. Before inline extraction this handoff had no node to attach to.
        Assert.NotNull(coupling);
        var keys = Assert.IsType<List<string>>(coupling!.Properties["dataKeys"]);
        Assert.Contains("catalog-count", keys);
    }

    [Fact]
    public void InlineScript_ShowsUpInTheMismatchReport()
    {
        var mismatches = new GraphQuery(_solutionGraph!).FindServerToJsMismatches().ToList();

        Assert.Contains(mismatches, m => m.JsNode.GetProperty<bool>("inline"));
    }
}
