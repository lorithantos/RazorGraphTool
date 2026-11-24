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
    /// <summary>
    /// Every selectable node type, for the tool descriptions and the refusal
    /// message.
    ///
    /// Hand-written because an attribute argument must be a constant, and
    /// therefore able to drift from the enum — which it did: NamedBinding shipped
    /// and was never listed here, so a caller reading the description had no way
    /// to know it could be asked for. NodeTypeListCoversEveryNodeType fails the
    /// build if a member is ever added without being named here, because a rule
    /// that has to be remembered every time is not a rule.
    /// </summary>
    internal const string NodeTypeList =
        "Project, RazorPage, PageModel, ApiController, ControllerAction, PartialView, ViewComponent, Layout, " +
        "Service, ServiceInterface, ServiceImplementation, ViewModel, Class, Method, Property, Field, " +
        "ViewDataKey, NamedBinding, ConfigurationFile, ExternalType, Parameter, Literal, Middleware, Route, " +
        "HtmlElement, TagHelperInvocation, JavaScriptFile, CssFile, XamlView, TextFile";

    internal const string GraphIdDescription =
        "Graph to query. Omit to use the most recently built or loaded graph.";

    internal const string DirectionDescription =
        "outgoing (default, the authored edge direction), incoming (who points at this node), or both.";

    internal static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The same as-given-then-decoded resolution as
    /// <see cref="NodeIdEscapes.TryResolve"/>, for a lookup that is not a graph:
    /// the method_body tools find their method in a compilation, and an escaped
    /// id has to reach them as well as it reaches get_node.
    /// </summary>
    internal static T? ResolveWith<T>(string id, Func<string, T?> lookup) where T : class
    {
        if (lookup(id) is { } found) return found;

        var decoded = NodeIdEscapes.Decode(id);
        return decoded == id ? null : lookup(decoded);
    }

    /// <summary>
    /// The id that actually resolved, or a refusal that says whether escaping
    /// was in play.
    /// </summary>
    /// <remarks>
    /// RETURNS the id rather than validating in place. It returned void until
    /// 2026-09-11, and every caller then went on to query the graph with its own
    /// raw argument — so the moment validation started accepting a second
    /// spelling, a caller would have validated one string and looked up another,
    /// turning the fix into an empty result. Use the value.
    /// </remarks>
    internal static string RequireNode(CodeGraph graph, string id)
    {
        if (NodeIdEscapes.TryResolve(graph, id, out var resolved)) return resolved;

        var decoded = NodeIdEscapes.Decode(id);
        var escaping = decoded == id
            ? string.Empty
            : " The id you passed carries escape sequences (&lt; and &gt;, or \\u003C and \\u003E, for the angle brackets of a generic);"
              + $" unescaped it reads '{decoded}', and that is not in this graph either.";

        throw new McpException($"Node not found: {id}.{escaping} Use find_nodes to discover ids.");
    }

    internal static NodeType ParseNodeType(string value) =>
        Enum.TryParse<NodeType>(value, ignoreCase: true, out var type) && type != NodeType.Unknown
            ? type
            : throw new McpException($"Unknown node type '{value}'. Valid types: {NodeTypeList}");

    /// <summary>
    /// Resolve a node kind against a specific graph, accepting both this build's
    /// vocabulary and any foreign kind the graph actually contains.
    ///
    /// Validated against the graph rather than the enum alone so the failure is
    /// specific: a kind that exists nowhere is refused with the foreign kinds
    /// present listed by name. Returning an empty result instead would be the
    /// absence-as-finding trap — indistinguishable from "that kind exists here
    /// and has no members".
    /// </summary>
    internal static string ResolveNodeKind(CodeGraph graph, string value)
    {
        var trimmed = (value ?? string.Empty).Trim();

        if (Enum.TryParse<NodeType>(trimmed, ignoreCase: true, out var known) && known != NodeType.Unknown)
            return known.ToString();

        // Matched against what is in THIS graph, and returned in the graph's own
        // casing: the caller gets back the string that reports display.
        var foreign = graph.ForeignNodeKinds
            .FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));
        if (foreign is not null) return foreign;

        var alsoAvailable = graph.ForeignNodeKinds.Count > 0
            ? $" This graph also carries foreign kinds, selectable by name: {string.Join(", ", graph.ForeignNodeKinds)}."
            : " This graph carries no foreign node kinds.";

        throw new McpException($"Unknown node type '{value}'. Valid types: {NodeTypeList}.{alsoAvailable}");
    }

    internal static TraversalDirection ParseDirection(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TraversalDirection.Outgoing
            : Enum.TryParse<TraversalDirection>(value.Trim(), ignoreCase: true, out var dir)
                ? dir
                : throw new McpException($"Unknown direction '{value}'. Valid values: outgoing, incoming, both.");
}
