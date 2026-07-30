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
}
