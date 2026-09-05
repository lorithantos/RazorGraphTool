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
    private static CodeGraph? _noTestsGraph;
    private static IReadOnlyList<string>? _noTestsSkipped;

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

            // The same solution without its tests — the lean navigation build.
            await using (var builder = new GraphBuilder { ExcludeTestProjects = true })
            {
                _noTestsGraph = await builder.BuildFromSolutionAllAsync(solutionPath);
                _noTestsSkipped = builder.SkippedTestProjects;
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
    public void Builds_DecoratedBy_ForTheHandWrittenAssemblyAttribute_AndNoGeneratedOnes()
    {
        // The SDK writes ~10 assembly attributes per project into obj\
        // (AssemblyInfo.cs, .NETCoreApp…AssemblyAttributes.cs); the fixture
        // hand-writes exactly one. Exactly one edge is therefore both the
        // positive case and the proof the generated-site gate held.
        var fromProjects = _solutionGraph!.Edges
            .Where(e => e.Type == EdgeType.DecoratedBy && e.FromId.StartsWith("proj:", StringComparison.Ordinal))
            .ToList();

        var edge = Assert.Single(fromProjects);
        Assert.Equal("proj:SampleLib", edge.FromId);
        Assert.Equal("ext:System.Reflection.AssemblyMetadataAttribute", edge.ToId);
        Assert.Equal("assembly", edge.GetProperty<string>("target"));
        Assert.Equal(new List<object?> { "fixture-marker", "sample-lib" }, edge.GetProperty<List<object?>>("args"));
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
            new[] { "Cache_SeedsItself", "Greet_ThroughTheInterface", "List_ReturnsSortedCatalogs", "Preload_CountsCatalogs", "Warm_IsPositive" },
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
    public void TypeNodes_SayWhetherTheyAreInterfaces()
    {
        // An interface that DI never registers is a plain Class node, so without
        // the flag it reads as a class: measured 30 of 37 on one app. The flag is
        // stamped on every type, so a graph built before it existed is told apart
        // from a class by the property being absent rather than false.
        var plainInterface = Type(_solutionGraph!, "SampleLib.IGreeter");
        var registeredInterface = Type(_solutionGraph!, "SampleLib.ICatalogStore");
        var implementation = Type(_solutionGraph!, "SampleLib.Greeter");

        Assert.Equal(NodeType.Class, plainInterface.Type);
        Assert.True(plainInterface.GetProperty<bool>("isInterface"));
        Assert.True(registeredInterface.GetProperty<bool>("isInterface"));
        Assert.False(implementation.GetProperty<bool>("isInterface"));
        Assert.True(implementation.Properties.ContainsKey("isInterface"));
    }

    private static GraphNode Type(CodeGraph graph, string fullName) =>
        graph.Nodes.Single(n => n.GetProperty<string>("fullName") == fullName);

    [Fact]
    public void AttributedDeclarations_ReportTheirOwnLine_NotTheAttributes()
    {
        // Attributes are part of the declaration node, not leading trivia, so a
        // syntax reference's span starts at the '[' and every attributed node
        // reported the attribute's line. Asserted against the source itself
        // rather than a hardcoded number, so editing the fixture cannot make
        // this pass by accident.
        var attributedType = Type(_solutionGraph!, "SampleLib.Greeter");
        Assert.Contains("class Greeter", SourceLine(attributedType), StringComparison.Ordinal);

        var attributedMethod = Method(_solutionGraph!, "SampleWeb.Tests.GreeterTests", "Greet_ThroughTheInterface");
        Assert.Contains("void Greet_ThroughTheInterface", SourceLine(attributedMethod), StringComparison.Ordinal);

        // An unattributed declaration was always right and must stay right.
        Assert.Contains("string Greet(", SourceLine(Method(_solutionGraph!, "SampleLib.Greeter", "Greet")));
    }

    /// <summary>The one line of source a node points at, read from disk.</summary>
    private static string SourceLine(GraphNode node) =>
        File.ReadAllLines(node.FilePath!)[node.LineStart!.Value - 1];

    [Fact]
    public void Methods_SayWhenTheirAccessibilityIsNotTheirOwn()
    {
        // An override takes the base declaration's accessibility; a positional
        // record's constructor takes the type header's. The visibility audit
        // needs both facts to stop offering edits that have no line.
        var primaryCtor = _solutionGraph!.GetNode("m:SampleLib.PriceTag..ctor(string,decimal)");
        Assert.NotNull(primaryCtor);
        Assert.True(primaryCtor!.GetProperty<bool>("isPrimaryConstructor"));

        Assert.True(Method(_solutionGraph, "SampleLib.Greeter", "ToString").GetProperty<bool>("isOverride"));

        var ordinary = Method(_solutionGraph, "SampleLib.Greeter", "Greet");
        Assert.False(ordinary.GetProperty<bool>("isOverride"));
        Assert.False(ordinary.GetProperty<bool>("isPrimaryConstructor"));
    }

    [Fact]
    public void CoversEdges_FollowInterfaceDispatch()
    {
        // The test binds to IGreeter.Greet and never names Greeter.Greet. Coverage
        // used to park on the interface member and report the implementation as
        // untested; the implementation is what runs, so it is what the test covers.
        var query = new GraphQuery(_solutionGraph!);
        var declaration = Method(_solutionGraph!, "SampleLib.IGreeter", "Greet");
        var implementation = Method(_solutionGraph!, "SampleLib.Greeter", "Greet");
        var beyond = Method(_solutionGraph!, "SampleLib.Greeter", "Shape");

        var onDeclaration = Assert.Single(query.GetCoveringTests(declaration.Id));
        var onImplementation = Assert.Single(query.GetCoveringTests(implementation.Id));
        var onBeyond = Assert.Single(query.GetCoveringTests(beyond.Id));

        Assert.Equal("Greet_ThroughTheInterface", onImplementation.Test.Name);
        // The dispatch step costs a hop, so depth keeps its meaning of "calls away".
        Assert.Equal(1, onDeclaration.Depth);
        Assert.Equal(2, onImplementation.Depth);
        Assert.Equal(3, onBeyond.Depth);
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
    public void MethodNodes_CarryBodyDepth()
    {
        // NestedFlow.Tally is deliberately foreach > if > for > if.
        var tally = Method(_solutionGraph!, "SampleLib.NestedFlow", "Tally");
        Assert.Equal(4, tally.GetProperty<int>("bodyDepth"));

        // Flat methods carry no property at all; typed access defaults to 0.
        var zero = Method(_solutionGraph!, "SampleLib.NestedFlow", "Zero");
        Assert.False(zero.Properties.ContainsKey("bodyDepth"));
        Assert.Equal(0, zero.GetProperty<int>("bodyDepth"));
    }

    [Fact]
    public void FindDeepMethods_ReportsOnlyDeepBodies_DeepestFirst()
    {
        var deep = new GraphQuery(_solutionGraph!).FindDeepMethods(4, "SampleLib").ToList();

        Assert.Contains(deep, m => m.Name == "Tally");
        Assert.DoesNotContain(deep, m => m.Name == "List");
        Assert.Equal(deep.OrderByDescending(m => m.GetProperty<int>("bodyDepth")).Select(m => m.Id), deep.Select(m => m.Id));
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

    // ---- Generated code ------------------------------------------------------

    [Fact]
    public void GeneratedRazorClasses_AreMarked_AndLinkedToTheirPage()
    {
        var page = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.RazorPage);

        // The Razor-layer page node and the Roslyn-layer generated class are
        // two views of the same artifact; the compiledInto edge is the join.
        var links = _solutionGraph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.References && e.GetProperty<bool>("compiledInto"))
            .Select(e => _solutionGraph.GetNode(e.ToId)!)
            .ToList();

        var generatedClass = Assert.Single(links);
        Assert.True(generatedClass.GetProperty<bool>("generated"));
        Assert.EndsWith(".g.cs", generatedClass.FilePath!, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Index.cshtml",
            generatedClass.GetProperty<string>("generatedFrom")!.Replace('\\', '/'),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void HandWrittenCode_IsNeverMarkedGenerated()
    {
        // The marker must not leak onto ordinary code: the PageModel lives in
        // Index.cshtml.cs, which is authored, not generated.
        var pageModel = _solutionGraph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");
        Assert.False(pageModel.GetProperty<bool>("generated"));

        var onGet = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet");
        Assert.False(onGet.GetProperty<bool>("generated"));
        Assert.EndsWith("Index.cshtml.cs", onGet.FilePath!.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
    }

    // ---- Excluding tests -----------------------------------------------------

    [Fact]
    public void NoTestsBuild_SkipsTestProjects_AndSaysSo()
    {
        var projects = _noTestsGraph!.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();

        // The test project was never compiled, and the skip is on record —
        // a graph missing its tests must say so.
        Assert.Equal(new[] { "SampleLib", "SampleWeb" }, projects);
        Assert.Equal(new[] { "SampleWeb.Tests" }, _noTestsSkipped);
    }

    [Fact]
    public void NoTestsBuild_HasNoTestMethodsAndNoCoversEdges()
    {
        Assert.DoesNotContain(_noTestsGraph!.NodesOfType(NodeType.Method), m => m.GetProperty<bool>("isTest"));
        Assert.DoesNotContain(_noTestsGraph!.Edges, e => e.Type == EdgeType.Covers);

        // Production topology is intact: the cross-project call is still there.
        var onGet = Method(_noTestsGraph!, "SampleWeb.Pages.IndexModel", "OnGet");
        Assert.Contains(_noTestsGraph!.Outgoing(onGet.Id),
            e => e.Type == EdgeType.Calls && _noTestsGraph.GetNode(e.ToId)!.Name == "List");
    }

    [Fact]
    public void CoverageQueries_RefuseTheNoTestsGraph()
    {
        // The guard that keeps "tests excluded" from ever reading as
        // "everything is uncovered".
        var query = new GraphQuery(_noTestsGraph!);
        var load = Method(_noTestsGraph!, "SampleLib.CatalogStore", "List");

        Assert.Throws<InvalidOperationException>(() => query.FindUncoveredMethods("SampleLib"));
        Assert.Throws<InvalidOperationException>(() => query.GetCoveringTests(load.Id));
    }

    // ---- Members: properties, fields, statics --------------------------------

    private static bool HasEdge(CodeGraph graph, string fromId, EdgeType type, string toId) =>
        graph.Edges.Any(e => e.Type == type && e.FromId == fromId && e.ToId == toId);

    [Fact]
    public void MemberNodes_ExistAndAreContainedByTheirType()
    {
        var basePrice = _solutionGraph!.GetNode("prop:SampleLib.PriceBook.BasePrice");
        Assert.NotNull(basePrice);
        Assert.Equal(NodeType.Property, basePrice!.Type);
        Assert.Equal("SampleLib.PriceBook", basePrice.GetProperty<string>("declaringType"));
        Assert.Equal("decimal", basePrice.GetProperty<string>("memberType"));
        Assert.True(HasEdge(_solutionGraph, "type:SampleLib.PriceBook", EdgeType.Contains, basePrice.Id));

        // A private field is a node too — the read/write story of a type runs
        // through its fields, whatever their accessibility.
        var store = _solutionGraph.GetNode("field:SampleLib.PriceBook._store");
        Assert.NotNull(store);
        Assert.Equal(NodeType.Field, store!.Type);
        Assert.False(store.GetProperty<bool>("isPublic"));
    }

    [Fact]
    public void StaticAndConstMembers_AreNodes_AndMarked()
    {
        var lookups = _solutionGraph!.GetNode("field:SampleLib.PriceBook.Lookups");
        Assert.True(lookups!.GetProperty<bool>("isStatic"));
        Assert.False(lookups.GetProperty<bool>("isConst"));

        var region = _solutionGraph.GetNode("prop:SampleLib.PriceBook.Region");
        Assert.True(region!.GetProperty<bool>("isStatic"));

        var defaultRegion = _solutionGraph.GetNode("field:SampleLib.PriceBook.DefaultRegion");
        Assert.True(defaultRegion!.GetProperty<bool>("isConst"));
        Assert.True(defaultRegion.GetProperty<bool>("isStatic"));
    }

    [Fact]
    public void RecordPositionalProperties_AreNodes_CompilerPlumbingIsNot()
    {
        Assert.NotNull(_solutionGraph!.GetNode("prop:SampleLib.PriceTag.Label"));
        Assert.NotNull(_solutionGraph.GetNode("prop:SampleLib.PriceTag.Amount"));

        Assert.Null(_solutionGraph.GetNode("prop:SampleLib.PriceTag.EqualityContract"));
        // No auto-property backing field may leak through as a Field node.
        Assert.DoesNotContain(_solutionGraph.NodesOfType(NodeType.Field), f => f.Name.Contains("k__BackingField"));
    }

    [Fact]
    public void ReadsAndWrites_AreAttributedToTheAccessingMethod()
    {
        // The DI idiom: the ctor writes the field, the method reads it.
        var ctorId = Method(_solutionGraph!, "SampleLib.PriceBook", ".ctor").Id;
        Assert.True(HasEdge(_solutionGraph!, ctorId, EdgeType.Writes, "field:SampleLib.PriceBook._store"));

        var totalId = Method(_solutionGraph!, "SampleLib.PriceBook", "Total").Id;
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "field:SampleLib.PriceBook._store"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "prop:SampleLib.PriceBook.BasePrice"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Writes, "prop:SampleLib.PriceBook.Current"));
    }

    [Fact]
    public void CompoundAssignment_YieldsBothReadAndWrite()
    {
        // Lookups++ reads the old value and writes the new one.
        var totalId = Method(_solutionGraph!, "SampleLib.PriceBook", "Total").Id;
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Reads, "field:SampleLib.PriceBook.Lookups"));
        Assert.True(HasEdge(_solutionGraph!, totalId, EdgeType.Writes, "field:SampleLib.PriceBook.Lookups"));
    }

    [Fact]
    public void ComputedProperty_ReadsItsInputs()
    {
        // Markup => BasePrice * 2: accessor bodies attribute to the property
        // node, because accessors are not Method nodes.
        Assert.True(HasEdge(_solutionGraph!,
            "prop:SampleLib.PriceBook.Markup", EdgeType.Reads, "prop:SampleLib.PriceBook.BasePrice"));
    }

    [Fact]
    public void StaticInitializerAccess_IsNotAttributedAnywhere()
    {
        // Region's initializer reads DefaultRegion, but it runs in the static
        // ctor, which is not a node. No edge may claim that access.
        Assert.DoesNotContain(_solutionGraph!.Edges, e =>
            e.ToId == "field:SampleLib.PriceBook.DefaultRegion" && e.Type is EdgeType.Reads or EdgeType.Writes);
    }

    [Fact]
    public void MemberDeclaredTypes_BecomeReferencesEdges()
    {
        // "Who uses PriceTag" was unanswerable while types only participated
        // in signatures: now the property typed by it references it…
        Assert.True(HasEdge(_solutionGraph!,
            "prop:SampleLib.PriceBook.Current", EdgeType.References, "type:SampleLib.PriceTag"));

        // …including through a List<> wrapper. History lives in the second
        // half of a partial class: exactly ONE edge, not one per declaration —
        // each declaration's SymbolInfo carries the full member list, and the
        // MVVM-toolkit generator makes partials the norm in WPF view models.
        Assert.Single(_solutionGraph!.Edges, e =>
            e.Type == EdgeType.References
            && e.FromId == "prop:SampleLib.PriceBook.History"
            && e.ToId == "type:SampleLib.PriceTag");
    }

    [Fact]
    public void MemberRead_CrossesProjectBoundary_InSolutionGraph()
    {
        // The member twin of Builds_CallEdge_FromWebProjectIntoClassLibrary:
        // OnGet reads SampleLib's static field across the project boundary.
        var onGetId = Method(_solutionGraph!, "SampleWeb.Pages.IndexModel", "OnGet").Id;
        Assert.True(HasEdge(_solutionGraph!, onGetId, EdgeType.Reads, "field:SampleLib.PriceBook.Lookups"));

        // And the control: the single-project build has no node to anchor it.
        var webOnlyOnGet = Method(_webOnlyGraph!, "SampleWeb.Pages.IndexModel", "OnGet").Id;
        Assert.DoesNotContain(_webOnlyGraph!.Edges, e =>
            e.FromId == webOnlyOnGet && e.Type == EdgeType.Reads && e.ToId.StartsWith("field:SampleLib."));
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
