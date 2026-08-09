namespace RazorGraph.Mcp;

using ModelContextProtocol;
using RazorGraph.Core.Graph;

/// <summary>
/// Argument parsing and validation shared by the tool classes, plus the
/// description strings for the parameters every tool repeats. Every tool takes
/// an optional graphId: omitting it means "the graph I most recently built or
/// loaded", which is what a single-graph session wants; naming one lets several
/// graphs be queried in the same session.
/// </summary>
internal static class ToolArguments
{
    internal const string NodeTypeList =
        "Project, RazorPage, PageModel, ApiController, ControllerAction, PartialView, ViewComponent, Layout, " +
        "Service, ServiceInterface, ServiceImplementation, ViewModel, Class, Method, Property, Field, " +
        "ViewDataKey, Middleware, Route, HtmlElement, TagHelperInvocation, JavaScriptFile, CssFile";

    internal const string GraphIdDescription =
        "Graph to query. Omit to use the most recently built or loaded graph.";

    internal const string DirectionDescription =
        "outgoing (default, the authored edge direction), incoming (who points at this node), or both.";

    internal static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    internal static void RequireNode(CodeGraph graph, string id)
    {
        if (!graph.HasNode(id))
            throw new McpException($"Node not found: {id}. Use find_nodes to discover ids.");
    }

    internal static NodeType ParseNodeType(string value) =>
        Enum.TryParse<NodeType>(value, ignoreCase: true, out var type) && type != NodeType.Unknown
            ? type
            : throw new McpException($"Unknown node type '{value}'. Valid types: {NodeTypeList}");

    internal static TraversalDirection ParseDirection(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TraversalDirection.Outgoing
            : Enum.TryParse<TraversalDirection>(value.Trim(), ignoreCase: true, out var dir)
                ? dir
                : throw new McpException($"Unknown direction '{value}'. Valid values: outgoing, incoming, both.");
}
