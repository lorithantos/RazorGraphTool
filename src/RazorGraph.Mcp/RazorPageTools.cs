namespace RazorGraph.Mcp;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    [Description("Backing infrastructure of a Razor page: its PageModel, ViewModel, and injected services. Only valid for RazorPage nodes. Each injectedServices entry says how it arrived in 'via': 'constructor' (a PageModel constructor parameter) or '@inject' (a directive in the .cshtml or .razor file, or inherited from a _ViewImports.cshtml or _Imports.razor above it, with its 'property', 'declaredIn' -- the project-relative path of the file whose directive it was -- and 'directiveLine' in that file). An @inject whose type has no node in the graph -- a framework service such as ILogger<T>, or a name nothing matches -- is listed in 'unresolvedInjections' instead. For a Blazor component (.razor) a 'caveat' says that its child components are not modelled and pageModel and viewModel do not apply.")]
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
            injectedServices = context.Injections.Select(InjectedService).ToList(),
            // Null, so omitted, when every @inject resolved -- a page with none
            // answers in the shape it always had.
            unresolvedInjections = context.UnresolvedInjections is { Count: > 0 } unresolved
                ? unresolved.Select(u => new
                {
                    name = u.Name,
                    declaredType = u.DeclaredType,
                    resolvedType = u.ResolvedType,
                    line = u.Line,
                    declaredIn = u.DeclaredIn,
                    via = "@inject"
                }).ToList()
                : null,
            caveat = context.Caveat
        });
    }

    /// <summary>
    /// A service's node summary plus how it reached the page. For an @inject,
    /// <c>property</c>, <c>declaredIn</c> and <c>directiveLine</c> say which line
    /// of which file declared it -- the page's own, or an imports file it
    /// inherits from; <c>name</c> and <c>line</c> stay the service's own, as
    /// everywhere else.
    /// </summary>
    private static JsonObject InjectedService(PageInjection injection)
    {
        var summary = JsonSerializer.SerializeToNode(ToolResponses.NodeSummary(injection.Service), ToolResponses.Json)!.AsObject();
        summary["via"] = injection.Via;
        if (injection.Name is not null) summary["property"] = injection.Name;
        if (injection.DeclaredIn is not null) summary["declaredIn"] = injection.DeclaredIn;
        if (injection.Line is not null) summary["directiveLine"] = injection.Line;
        return summary;
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
            ? message + " It is a Blazor component (.razor) without @page. Its @inject services are this node's incoming InjectedInto edges (get_node with edges=incoming, edgeType=InjectedInto), and any whose type has no node are in its unresolvedInjections property."
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
