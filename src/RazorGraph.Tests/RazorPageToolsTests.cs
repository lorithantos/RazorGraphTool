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
/// not modelled (found 2026-11-09 on a Blazor Server app). @inject is modelled
/// now, so page_context lists those services; what is still not modelled --
/// child components -- carries a caveat, and a Razor Pages answer carries none.
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
        var component = new GraphNode
        {
            Id = ComponentId, Type = NodeType.RazorPage, Name = "GreetingPanel",
            FilePath = @"C:\app\Components\GreetingPanel.razor"
        };
        component.SetProperty("unresolvedInjections", new List<object>
        {
            new Dictionary<string, object>
            {
                ["name"] = "Logger", ["declaredType"] = "ILogger<GreetingPanel>", ["line"] = 4,
                ["typeFrom"] = "generatedProperty",
                ["resolvedType"] = "Microsoft.Extensions.Logging.ILogger<App.Components.GreetingPanel>"
            }
        });
        graph.AddNode(component);
        graph.AddNode(new GraphNode { Id = "svc:App.IGreetingService", Type = NodeType.ServiceInterface, Name = "IGreetingService" });
        graph.AddEdge(new GraphEdge
        {
            FromId = "svc:App.IGreetingService", ToId = ComponentId, Type = EdgeType.InjectedInto,
            Properties = { ["via"] = "@inject", ["name"] = "Greetings", ["declaredType"] = "IGreetingService", ["line"] = 3, ["typeFrom"] = "generatedProperty" }
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

    /// <summary>
    /// A component's @inject services are listed -- the in-solution one with
    /// how it arrived and the line that declared it, the framework one under
    /// unresolvedInjections -- and the caveat that remains is about children
    /// only: it no longer says injections are not modelled.
    /// </summary>
    [Fact]
    public void PageContext_OnAComponent_ListsItsInjects_AndCaveatsOnlyItsChildren()
    {
        using var doc = JsonDocument.Parse(ToolsOver().PageContext(ComponentId));
        var root = doc.RootElement;

        var service = Assert.Single(root.GetProperty("injectedServices").EnumerateArray());
        Assert.Equal("IGreetingService", service.GetProperty("name").GetString());
        Assert.Equal("@inject", service.GetProperty("via").GetString());
        Assert.Equal("Greetings", service.GetProperty("property").GetString());
        Assert.Equal(3, service.GetProperty("directiveLine").GetInt32());

        var unresolved = Assert.Single(root.GetProperty("unresolvedInjections").EnumerateArray());
        Assert.Equal("Logger", unresolved.GetProperty("name").GetString());
        Assert.Equal("ILogger<GreetingPanel>", unresolved.GetProperty("declaredType").GetString());
        Assert.Equal("Microsoft.Extensions.Logging.ILogger<App.Components.GreetingPanel>",
            unresolved.GetProperty("resolvedType").GetString());
        Assert.Equal(4, unresolved.GetProperty("line").GetInt32());

        Assert.Equal(GraphQuery.ComponentContextCaveat, root.GetProperty("caveat").GetString());
        Assert.DoesNotContain("inject", GraphQuery.ComponentContextCaveat, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("child components", GraphQuery.ComponentContextCaveat, StringComparison.Ordinal);
        Assert.Contains("not modelled", GraphQuery.ComponentContextCaveat, StringComparison.Ordinal);
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
        Assert.DoesNotContain("not modelled", message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A Razor page with no @inject keeps its shape: no caveat, no
    /// unresolvedInjections, and its constructor service now says how it arrived.
    /// </summary>
    [Fact]
    public void PageContext_OnARazorPage_KeepsItsShape_AndSaysTheServiceCameByConstructor()
    {
        using var doc = JsonDocument.Parse(ToolsOver().PageContext(PageId));
        var root = doc.RootElement;

        Assert.False(root.TryGetProperty("caveat", out _));
        Assert.Equal(["page", "pageModel", "injectedServices"],
            root.EnumerateObject().Select(p => p.Name).ToArray());
        var service = root.GetProperty("injectedServices")[0];
        Assert.Equal("ICatalogService", service.GetProperty("name").GetString());
        Assert.Equal("constructor", service.GetProperty("via").GetString());
        Assert.False(service.TryGetProperty("property", out _));
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
