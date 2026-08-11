namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;

/// <summary>
/// Tools for the Razor page surface: what a page renders, what backs it, and
/// where server-prepared data leaks into client JavaScript.
/// </summary>
[McpServerToolType]
public sealed class RazorPageTools(GraphStore store)
{
    [McpServerTool(Name = "render_tree")]
    [Description("Render dependencies of a Razor page: layout, partials, sections, and components, traversed to depth 5.")]
    public string RenderTree(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, pageId);
        var query = new GraphQuery(graph);
        var items = query.GetRenderTree(pageId)
            .Select(t => new { edgeType = t.Edge.DisplayType, node = ToolResponses.NodeSummary(t.Node) })
            .ToList();
        return ToolResponses.ToJson(new { pageId, returned = items.Count, items });
    }

    [McpServerTool(Name = "page_context")]
    [Description("Backing infrastructure of a Razor page: its PageModel, ViewModel, and injected services. Only valid for RazorPage nodes.")]
    public string PageContext(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, pageId);
        var context = new GraphQuery(graph).GetPageContext(pageId)
            ?? throw new McpException($"Node '{pageId}' is not a RazorPage; page_context only applies to RazorPage nodes.");

        return ToolResponses.ToJson(new
        {
            page = ToolResponses.NodeSummary(context.Page),
            pageModel = context.PageModel is null ? null : ToolResponses.NodeSummary(context.PageModel),
            viewModel = context.ViewModel is null ? null : ToolResponses.NodeSummary(context.ViewModel),
            injectedServices = context.InjectedServices.Select(ToolResponses.NodeSummary).ToList()
        });
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
