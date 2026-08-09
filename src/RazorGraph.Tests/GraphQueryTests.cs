namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using Xunit;

public class GraphQueryTests
{
    /// <summary>
    /// A hand-built graph mirroring what GraphBuilder produces:
    /// page -> PageModel (PageServedBy), service -> PageModel (InjectedInto),
    /// page -> layout/partial (render edges), page -> JS (ViewDataReadBy).
    /// </summary>
    private static CodeGraph BuildSampleGraph()
    {
        var graph = new CodeGraph();

        var page = new GraphNode { Id = "page:Pages/Catalog.cshtml", Type = NodeType.RazorPage, Name = "Catalog" };
        page.SetProperty("modelType", "CatalogViewModel");

        graph.AddNode(page);
        graph.AddNode(new GraphNode { Id = "pm:App.CatalogModel", Type = NodeType.PageModel, Name = "CatalogModel" });
        graph.AddNode(new GraphNode { Id = "vm:App.CatalogViewModel", Type = NodeType.ViewModel, Name = "CatalogViewModel" });
        graph.AddNode(new GraphNode { Id = "svc:App.ICatalogService", Type = NodeType.ServiceInterface, Name = "ICatalogService" });
        graph.AddNode(new GraphNode { Id = "page:Pages/Shared/_Layout.cshtml", Type = NodeType.PartialView, Name = "_Layout" });
        graph.AddNode(new GraphNode { Id = "page:Pages/Shared/_Card.cshtml", Type = NodeType.PartialView, Name = "_Card" });
        graph.AddNode(new GraphNode { Id = "js:wwwroot/js/catalog.js", Type = NodeType.JavaScriptFile, Name = "catalog.js" });

        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Catalog.cshtml", ToId = "pm:App.CatalogModel", Type = EdgeType.PageServedBy });
        graph.AddEdge(new GraphEdge { FromId = "pm:App.CatalogModel", ToId = "page:Pages/Catalog.cshtml", Type = EdgeType.ReturnsView });
        graph.AddEdge(new GraphEdge { FromId = "svc:App.ICatalogService", ToId = "pm:App.CatalogModel", Type = EdgeType.InjectedInto });
        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Catalog.cshtml", ToId = "page:Pages/Shared/_Layout.cshtml", Type = EdgeType.UsesLayout });
        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Shared/_Layout.cshtml", ToId = "page:Pages/Shared/_Card.cshtml", Type = EdgeType.RendersPartial });
        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Catalog.cshtml", ToId = "js:wwwroot/js/catalog.js", Type = EdgeType.ViewDataReadBy });

        return graph;
    }

    /// <summary>A hand-built escape graph: one depth-0 self-escape at Main, one depth-2 chain into an event handler.</summary>
    private static CodeGraph BuildEscapeGraph()
    {
        var graph = new CodeGraph();

        var main = new GraphNode { Id = "m:Cli.Program.Main()", Type = NodeType.Method, Name = "Main" };
        main.SetProperty("entryPointKind", "main");
        main.SetProperty("project", "Cli");
        graph.AddNode(main);

        var handler = new GraphNode { Id = "m:Web.Widget.OnTick(object,System.EventArgs)", Type = NodeType.Method, Name = "OnTick" };
        handler.SetProperty("entryPointKind", "eventHandler");
        handler.SetProperty("project", "Web");
        graph.AddNode(handler);

        graph.AddNode(new GraphNode { Id = "m:Lib.Thrower.Boom()", Type = NodeType.Method, Name = "Boom" });

        var selfEscape = new GraphEdge { FromId = "m:Cli.Program.Main()", ToId = "m:Cli.Program.Main()", Type = EdgeType.Escapes };
        selfEscape.Properties["exceptionType"] = "System.Exception";
        selfEscape.Properties["depth"] = 0;
        graph.AddEdge(selfEscape);

        var chained = new GraphEdge { FromId = "m:Lib.Thrower.Boom()", ToId = "m:Web.Widget.OnTick(object,System.EventArgs)", Type = EdgeType.Escapes };
        chained.Properties["exceptionType"] = "Lib.FooException";
        chained.Properties["depth"] = 2;
        graph.AddEdge(chained);

        return graph;
    }

    [Fact]
    public void FindEscapingExceptions_OrdersShallowestFirstAndResolvesNodes()
    {
        var escapes = new GraphQuery(BuildEscapeGraph()).FindEscapingExceptions().ToList();

        Assert.Equal(2, escapes.Count);
        Assert.Equal("Main", escapes[0].EntryPoint.Name);   // depth 0 before depth 2
        Assert.Equal("Main", escapes[0].Thrower.Name);      // a self-escape names itself
        Assert.Equal("Boom", escapes[1].Thrower.Name);
        Assert.Equal("OnTick", escapes[1].EntryPoint.Name);
    }

    [Fact]
    public void FindEscapingExceptions_EveryFilterNarrows()
    {
        var query = new GraphQuery(BuildEscapeGraph());

        Assert.Single(query.FindEscapingExceptions(entryPointKind: "eventHandler"));
        Assert.Single(query.FindEscapingExceptions(exceptionTypeContains: "fooex")); // case-insensitive
        Assert.Single(query.FindEscapingExceptions(project: "Cli"));
        Assert.Single(query.FindEscapingExceptions(entryPointId: "m:Cli.Program.Main()"));
        Assert.Empty(query.FindEscapingExceptions(entryPointKind: "pageHandler"));
    }

    [Fact]
    public void FindNodes_FiltersByTypeAndName()
    {
        var query = new GraphQuery(BuildSampleGraph());

        Assert.Single(query.FindNodes(NodeType.RazorPage));
        Assert.Single(query.FindNodes(NodeType.PartialView, "card"));
        Assert.Empty(query.FindNodes(NodeType.PartialView, "nonexistent"));
    }

    [Fact]
    public void GetNeighbors_FiltersByEdgeType()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var served = query.GetNeighbors("page:Pages/Catalog.cshtml", EdgeType.PageServedBy).ToList();

        Assert.Single(served);
        Assert.Equal("CatalogModel", served[0].Target.Name);
    }

    [Fact]
    public void GetPredecessors_FindsIncomingEdges()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var injectors = query.GetPredecessors("pm:App.CatalogModel", EdgeType.InjectedInto).ToList();

        Assert.Single(injectors);
        Assert.Equal("ICatalogService", injectors[0].Source.Name);
    }

    [Fact]
    public void GetRenderTree_FollowsRenderEdgesOnly()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var tree = query.GetRenderTree("page:Pages/Catalog.cshtml").Select(t => t.Node.Name).ToList();

        Assert.Contains("_Layout", tree);
        Assert.Contains("_Card", tree);       // transitively via the layout
        Assert.DoesNotContain("CatalogModel", tree); // PageServedBy is not a render edge
    }

    [Fact]
    public void GetPageContext_ReturnsModelServicesAndViewModel()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var context = query.GetPageContext("page:Pages/Catalog.cshtml");

        Assert.NotNull(context);
        Assert.Equal("CatalogModel", context!.PageModel?.Name);
        // Regression: property key is "modelType" (was read as "model_type")
        Assert.Equal("CatalogViewModel", context.ViewModel?.Name);
        // Regression: InjectedInto edges point service -> consumer (predecessors)
        Assert.Single(context.InjectedServices);
        Assert.Equal("ICatalogService", context.InjectedServices[0].Name);
    }

    [Fact]
    public void GetPageContext_ReturnsNullForNonPage()
    {
        var query = new GraphQuery(BuildSampleGraph());

        Assert.Null(query.GetPageContext("pm:App.CatalogModel"));
        Assert.Null(query.GetPageContext("missing"));
    }

    [Fact]
    public void TraceDataFlow_RespectsDepth()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b", "c", "d" })
            graph.AddNode(new GraphNode { Id = id, Type = NodeType.Class, Name = id });
        graph.AddEdge(new GraphEdge { FromId = "a", ToId = "b", Type = EdgeType.Calls });
        graph.AddEdge(new GraphEdge { FromId = "b", ToId = "c", Type = EdgeType.Reads });
        graph.AddEdge(new GraphEdge { FromId = "c", ToId = "d", Type = EdgeType.Writes });

        var query = new GraphQuery(graph);
        var reached = query.TraceDataFlow("a", maxDepth: 2).Select(t => t.Node.Id).ToList();

        Assert.Equal(new[] { "b", "c" }, reached);
    }

    [Fact]
    public void FindServerToJsMismatches_YieldsViewDataReadPairs()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var mismatches = query.FindServerToJsMismatches().ToList();

        Assert.Single(mismatches);
        Assert.Equal("Catalog", mismatches[0].ServerNode.Name);
        Assert.Equal("catalog.js", mismatches[0].JsNode.Name);
    }

    [Fact]
    public void TraceDataFlow_DescendsIntoContainedMethods()
    {
        // Regression: Calls edges hang off Method nodes, so a trace starting at a
        // PageModel reported nothing but its views — the calls its own handler
        // made were one un-followed Contains edge away.
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "pm:App.CatalogModel", Type = NodeType.PageModel, Name = "CatalogModel" });
        graph.AddNode(new GraphNode { Id = "m:OnGet", Type = NodeType.Method, Name = "OnGet" });
        graph.AddNode(new GraphNode { Id = "m:LoadCatalog", Type = NodeType.Method, Name = "LoadCatalog" });
        graph.AddEdge(new GraphEdge { FromId = "pm:App.CatalogModel", ToId = "m:OnGet", Type = EdgeType.Contains });
        graph.AddEdge(new GraphEdge { FromId = "m:OnGet", ToId = "m:LoadCatalog", Type = EdgeType.Calls });

        var reached = new GraphQuery(graph)
            .TraceDataFlow("pm:App.CatalogModel", maxDepth: 1)
            .Select(t => t.Node.Name)
            .ToList();

        Assert.Contains("OnGet", reached);
        Assert.Contains("LoadCatalog", reached);
    }

    [Fact]
    public void TraceDataFlow_Incoming_FindsInjectedServiceConsumers()
    {
        var query = new GraphQuery(BuildSampleGraph());

        var reached = query
            .TraceDataFlow("pm:App.CatalogModel", maxDepth: 1, TraversalDirection.Incoming)
            .Select(t => t.Node.Name)
            .ToList();

        // Outgoing cannot answer this: InjectedInto points service -> consumer.
        Assert.Contains("ICatalogService", reached);
    }

    // ---- Coverage ------------------------------------------------------------

    /// <summary>
    /// Two production methods, one test. The test covers Load directly and
    /// Normalize through it; Orphan is reached by nothing.
    /// </summary>
    private static CodeGraph BuildCoverageGraph()
    {
        var graph = new CodeGraph();

        var test = new GraphNode { Id = "m:Tests.ListWorks()", Type = NodeType.Method, Name = "ListWorks" };
        test.SetProperty("isTest", true);
        test.SetProperty("project", "App.Tests");
        graph.AddNode(test);

        foreach (var (id, name) in new[] { ("m:Lib.Load()", "Load"), ("m:Lib.Normalize()", "Normalize"), ("m:Lib.Orphan()", "Orphan") })
        {
            var node = new GraphNode { Id = id, Type = NodeType.Method, Name = name };
            node.SetProperty("project", "Lib");
            graph.AddNode(node);
        }

        graph.AddEdge(new GraphEdge
        {
            FromId = "m:Tests.ListWorks()", ToId = "m:Lib.Load()",
            Type = EdgeType.Covers, Properties = { ["depth"] = 1 }
        });
        graph.AddEdge(new GraphEdge
        {
            FromId = "m:Tests.ListWorks()", ToId = "m:Lib.Normalize()",
            Type = EdgeType.Covers, Properties = { ["depth"] = 2 }
        });

        return graph;
    }

    [Fact]
    public void GetCoveringTests_ReturnsTestsNearestFirst()
    {
        var query = new GraphQuery(BuildCoverageGraph());

        var covering = query.GetCoveringTests("m:Lib.Load()").ToList();

        Assert.Single(covering);
        Assert.Equal("ListWorks", covering[0].Test.Name);
        Assert.Equal(1, covering[0].Depth);
        Assert.Empty(query.GetCoveringTests("m:Lib.Orphan()"));
    }

    [Fact]
    public void GetCoveredMethods_OrdersByDepth()
    {
        var query = new GraphQuery(BuildCoverageGraph());

        var covered = query.GetCoveredMethods("m:Tests.ListWorks()").ToList();

        Assert.Equal(new[] { "Load", "Normalize" }, covered.Select(c => c.Method.Name));
        Assert.Equal(new[] { 1, 2 }, covered.Select(c => c.Depth));
    }

    [Fact]
    public void FindUncoveredMethods_ExcludesTestsAndCoveredCode()
    {
        var query = new GraphQuery(BuildCoverageGraph());

        var uncovered = query.FindUncoveredMethods("Lib").Select(m => m.Name).ToList();

        Assert.Equal(new[] { "Orphan" }, uncovered);

        // The test method itself is never "uncovered production code".
        Assert.DoesNotContain("ListWorks", query.FindUncoveredMethods().Select(m => m.Name));
    }

    [Fact]
    public void ComputeRelevance_ScoresFocusFullAndDecaysByDepth_BestWins()
    {
        // A -> B -> C, and D -> C. Focusing A and D: C is depth 2 from A
        // (score 1/3) but depth 1 from D (score 1/2) — the nearest wins.
        var graph = new CodeGraph();
        foreach (var id in new[] { "m:A()", "m:B()", "m:C()", "m:D()" })
            graph.AddNode(new GraphNode { Id = id, Type = NodeType.Method, Name = id });
        graph.AddEdge(new GraphEdge { FromId = "m:A()", ToId = "m:B()", Type = EdgeType.Calls });
        graph.AddEdge(new GraphEdge { FromId = "m:B()", ToId = "m:C()", Type = EdgeType.Calls });
        graph.AddEdge(new GraphEdge { FromId = "m:D()", ToId = "m:C()", Type = EdgeType.Calls });

        var (relevance, missing) = new GraphQuery(graph)
            .ComputeRelevance(new[] { "m:A()", "m:D()", "m:Nope()" }, maxDepth: 3);

        // Missing ids are data for the caller's policy, never an exception here.
        Assert.Equal(new[] { "m:Nope()" }, missing);
        Assert.Equal(1.0, relevance["m:A()"]);
        Assert.Equal(1.0, relevance["m:D()"]);
        Assert.Equal(0.5, relevance["m:B()"]);
        Assert.Equal(0.5, relevance["m:C()"]);
    }

    [Fact]
    public void CoverageQueries_RefuseAGraphWithNoTestMethods()
    {
        // A graph with no test methods cannot answer coverage questions — an
        // empty answer would read as "everything is uncovered". The refusal
        // must be loud, and it must be eager: the throw happens at the call,
        // not when the result is finally enumerated.
        var graph = new CodeGraph();
        var node = new GraphNode { Id = "m:Lib.Load()", Type = NodeType.Method, Name = "Load" };
        node.SetProperty("project", "Lib");
        graph.AddNode(node);

        var query = new GraphQuery(graph);

        Assert.Throws<InvalidOperationException>(() => query.GetCoveringTests("m:Lib.Load()"));
        Assert.Throws<InvalidOperationException>(() => query.GetCoveredMethods("m:Lib.Load()"));
        Assert.Throws<InvalidOperationException>(() => query.FindUncoveredMethods("Lib"));
    }
}
