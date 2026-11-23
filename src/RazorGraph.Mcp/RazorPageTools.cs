namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;

/// <summary>
/// Tools for the Razor page surface: what a page renders, what backs it, and
/// where server-prepared data leaks into client JavaScript.
/// </summary>
[McpServerToolType]
internal sealed class RazorPageTools(GraphStore store)
{
    [McpServerTool(Name = "render_tree")]
    [Description("Render dependencies of a Razor page: layout, partials, sections, and components, traversed to depth 5. For a Blazor component (.razor) a 'caveat' says that its child components are not modelled, so an empty list is not read as 'renders nothing'.")]
    public string RenderTree(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        pageId = ToolArguments.RequireNode(graph, pageId);
        var query = new GraphQuery(graph);
        var items = query.GetRenderTree(pageId)
            .Select(t => new { edgeType = t.Edge.DisplayType, node = ToolResponses.NodeSummary(t.Node) })
            .ToList();
        // Null for a .cshtml page, and a null is not written, so a Razor Pages
        // answer is byte-for-byte what it was before components were flagged.
        return ToolResponses.ToJson(new { pageId, returned = items.Count, items, caveat = query.GetRenderTreeCaveat(pageId) });
    }

    [McpServerTool(Name = "page_context")]
    [Description("Backing infrastructure of a Razor page: its PageModel, ViewModel, and injected services. Only valid for RazorPage nodes. For a Blazor component (.razor) a 'caveat' says that its @inject directives are not modelled, so an empty injectedServices is not read as 'injects nothing'.")]
    public string PageContext(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        pageId = ToolArguments.RequireNode(graph, pageId);
        var context = new GraphQuery(graph).GetPageContext(pageId)
            ?? throw new McpException(NotAPageMessage(graph.GetNode(pageId)!, pageId));

        return ToolResponses.ToJson(new
        {
            page = ToolResponses.NodeSummary(context.Page),
            pageModel = context.PageModel is null ? null : ToolResponses.NodeSummary(context.PageModel),
            viewModel = context.ViewModel is null ? null : ToolResponses.NodeSummary(context.ViewModel),
            // Lambda, not a method group: NodeSummary's optional freshness
            // parameter makes the group ambiguous against Select's indexed overload.
            injectedServices = context.InjectedServices.Select(n => ToolResponses.NodeSummary(n)).ToList(),
            caveat = context.Caveat
        });
    }

    /// <summary>
    /// The refusal for a node page_context does not apply to. A Blazor component
    /// without @page is a PartialView here, and "not a RazorPage" alone would
    /// send its caller looking for the page it belongs to rather than at the gap.
    /// </summary>
    private static string NotAPageMessage(GraphNode node, string pageId)
    {
        var message = $"Node '{pageId}' is not a RazorPage; page_context only applies to RazorPage nodes.";
        return GraphQuery.IsBlazorComponent(node)
            ? message + " It is a Blazor component (.razor) without @page; a component's @inject directives are not modelled in any case, so read them in the file."
            : message;
    }

    [McpServerTool(Name = "find_server_to_js_mismatches")]
    [Description("Anti-pattern report: server-prepared data (ViewData/model properties) consumed by client-side JavaScript, including inline <script> blocks.")]
    public string FindServerToJsMismatches([Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var items = new GraphQuery(graph).FindServerToJsMismatches()
            .Select(m => new { server = ToolResponses.NodeSummary(m.ServerNode), js = ToolResponses.NodeSummary(m.JsNode), edgeType = m.Edge.DisplayType })
            .ToList();
        return ToolResponses.ToJson(new { returned = items.Count, items });
    }
}
