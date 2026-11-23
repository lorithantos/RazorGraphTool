namespace RazorGraph.Tests;

using System.Text.Json;
using ModelContextProtocol;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// page_context and render_tree over a Blazor component. Both used to answer a
/// component as if it were a Razor page with nothing behind it: a page with 16
/// @inject lines came back with injectedServices [] and no word that @inject is
/// not modelled (found 2026-11-09 on a Blazor Server app). An empty list there
/// reads as a fact about the component, so the answer now carries a caveat, and
/// a Razor Pages answer carries none -- its JSON is what it was.
/// </summary>
public class RazorPageToolsTests
{
    private const string ComponentId = "page:Components/GreetingPanel.razor";
    private const string ChildId = "page:Components/GreetingBadge.razor";
    private const string PageId = "page:Pages/Catalog.cshtml";

    /// <summary>
    /// The shapes the extractor produces: a .razor with @page is a RazorPage
    /// with no edges that page_context or render_tree follow, one without is a
    /// PartialView, and a .cshtml page has its PageModel, a constructor-injected
    /// service and a partial.
    /// </summary>
    private static RazorPageTools ToolsOver()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode
        {
            Id = ComponentId, Type = NodeType.RazorPage, Name = "GreetingPanel",
            FilePath = @"C:\app\Components\GreetingPanel.razor"
        });
        graph.AddNode(new GraphNode
        {
            Id = ChildId, Type = NodeType.PartialView, Name = "GreetingBadge",
            FilePath = @"C:\app\Components\GreetingBadge.razor"
        });

        graph.AddNode(new GraphNode { Id = PageId, Type = NodeType.RazorPage, Name = "Catalog", FilePath = @"C:\app\Pages\Catalog.cshtml" });
        graph.AddNode(new GraphNode { Id = "pm:App.CatalogModel", Type = NodeType.PageModel, Name = "CatalogModel" });
        graph.AddNode(new GraphNode { Id = "svc:App.ICatalogService", Type = NodeType.ServiceInterface, Name = "ICatalogService" });
        graph.AddNode(new GraphNode { Id = "page:Pages/Shared/_Card.cshtml", Type = NodeType.PartialView, Name = "_Card" });
        graph.AddEdge(new GraphEdge { FromId = PageId, ToId = "pm:App.CatalogModel", Type = EdgeType.PageServedBy });
        graph.AddEdge(new GraphEdge { FromId = "svc:App.ICatalogService", ToId = "pm:App.CatalogModel", Type = EdgeType.InjectedInto });
        graph.AddEdge(new GraphEdge { FromId = PageId, ToId = "page:Pages/Shared/_Card.cshtml", Type = EdgeType.RendersPartial });

        var store = new GraphStore();
        store.Add(graph, source: "test://hand-built", requestedId: "g");
        return new RazorPageTools(store);
    }

    [Fact]
    public void PageContext_OnAComponent_SaysItsInjectionsAreNotModelled()
    {
        using var doc = JsonDocument.Parse(ToolsOver().PageContext(ComponentId));
        var root = doc.RootElement;

        // The list is still empty -- nothing is invented -- but it no longer
        // stands alone.
        Assert.Equal(0, root.GetProperty("injectedServices").GetArrayLength());
        Assert.Equal(GraphQuery.ComponentInjectionsCaveat, root.GetProperty("caveat").GetString());
        Assert.Contains("@inject", GraphQuery.ComponentInjectionsCaveat, StringComparison.Ordinal);
        Assert.Contains("not modelled", GraphQuery.ComponentInjectionsCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public void RenderTree_OnAComponent_SaysItsChildComponentsAreNotModelled()
    {
        var tools = ToolsOver();

        foreach (var id in new[] { ComponentId, ChildId })
        {
            using var doc = JsonDocument.Parse(tools.RenderTree(id));
            var root = doc.RootElement;

            Assert.Equal(0, root.GetProperty("returned").GetInt32());
            Assert.Equal(GraphQuery.ComponentChildrenCaveat, root.GetProperty("caveat").GetString());
        }

        Assert.Contains("child components", GraphQuery.ComponentChildrenCaveat, StringComparison.Ordinal);
        Assert.Contains("not modelled", GraphQuery.ComponentChildrenCaveat, StringComparison.Ordinal);
    }

    [Fact]
    public void PageContext_OnAComponentWithoutPage_RefusesNamingTheComponentGap()
    {
        var message = Assert.Throws<McpException>(() => ToolsOver().PageContext(ChildId)).Message;

        Assert.Contains("is not a RazorPage", message, StringComparison.Ordinal);
        Assert.Contains("Blazor component", message, StringComparison.Ordinal);
        Assert.Contains("@inject", message, StringComparison.Ordinal);
    }

    [Fact]
    public void PageContext_OnARazorPage_IsUnchanged()
    {
        using var doc = JsonDocument.Parse(ToolsOver().PageContext(PageId));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("caveat", out _));
        Assert.Equal(["page", "pageModel", "injectedServices"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("ICatalogService", root.GetProperty("injectedServices")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void RenderTree_OnARazorPage_IsUnchanged()
    {
        using var doc = JsonDocument.Parse(ToolsOver().RenderTree(PageId));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("caveat", out _));
        Assert.Equal(["pageId", "returned", "items"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("returned").GetInt32());
    }

    [Fact]
    public void PageContext_OnANonPageThatIsNotAComponent_KeepsItsRefusal()
    {
        var message = Assert.Throws<McpException>(() => ToolsOver().PageContext("page:Pages/Shared/_Card.cshtml")).Message;

        Assert.Equal("Node 'page:Pages/Shared/_Card.cshtml' is not a RazorPage; page_context only applies to RazorPage nodes.", message);
    }
}
