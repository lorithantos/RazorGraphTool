namespace RazorGraph.Core.Query;

using RazorGraph.Core.Graph;

/// <summary>
/// High-level query surface over a CodeGraph. Designed to return
/// small, relevant result sets for LLM consumption.
/// </summary>
public sealed class GraphQuery
{
    private readonly CodeGraph _graph;

    public GraphQuery(CodeGraph graph) => _graph = graph;

    /// <summary>
    /// Get a single node by its stable ID.
    /// </summary>
    public GraphNode? GetNode(string id) => _graph.GetNode(id);

    /// <summary>
    /// Find nodes by type, optionally filtered by name substring.
    /// </summary>
    public IEnumerable<GraphNode> FindNodes(NodeType type, string? nameContains = null)
    {
        var query = _graph.NodesOfType(type);
        if (!string.IsNullOrWhiteSpace(nameContains))
            query = query.Where(n => n.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        return query;
    }

    /// <summary>
    /// Get all direct neighbors of a node via outgoing edges of given types.
    /// </summary>
    public IEnumerable<(GraphEdge Edge, GraphNode Target)> GetNeighbors(
        string nodeId,
        params EdgeType[] edgeTypes)
    {
        var filter = edgeTypes.Length > 0 ? new HashSet<EdgeType>(edgeTypes) : null;
        foreach (var edge in _graph.Outgoing(nodeId))
        {
            if (filter != null && !filter.Contains(edge.Type)) continue;
            var target = _graph.GetNode(edge.ToId);
            if (target != null) yield return (edge, target);
        }
    }

    /// <summary>
    /// Get all nodes that point TO this node via given edge types.
    /// </summary>
    public IEnumerable<(GraphEdge Edge, GraphNode Source)> GetPredecessors(
        string nodeId,
        params EdgeType[] edgeTypes)
    {
        var filter = edgeTypes.Length > 0 ? new HashSet<EdgeType>(edgeTypes) : null;
        foreach (var edge in _graph.Incoming(nodeId))
        {
            if (filter != null && !filter.Contains(edge.Type)) continue;
            var source = _graph.GetNode(edge.FromId);
            if (source != null) yield return (edge, source);
        }
    }

    /// <summary>
    /// Trace data flow: find all nodes reachable from start via Reads/Writes/BindsTo/Calls edges.
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge, int Depth)> TraceDataFlow(string startId, int maxDepth = 3)
    {
        var filter = new HashSet<EdgeType>
        {
            EdgeType.Reads, EdgeType.Writes, EdgeType.BindsTo,
            EdgeType.Calls, EdgeType.InjectedInto, EdgeType.ReturnsView
        };
        return _graph.Traverse(startId, filter, maxDepth);
    }

    /// <summary>
    /// Find all render dependencies of a Razor page (layout, partials, sections, components).
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge)> GetRenderTree(string pageId)
    {
        var filter = new HashSet<EdgeType>
        {
            EdgeType.UsesLayout, EdgeType.RendersPartial,
            EdgeType.RendersComponent, EdgeType.DefinesSection
        };
        return _graph.Traverse(pageId, filter, maxDepth: 5)
            .Select(t => (t.Node, t.Edge));
    }

    /// <summary>
    /// Find the PageModel, Services, and ViewModel for a given Razor page.
    /// </summary>
    public PageContext? GetPageContext(string pageId)
    {
        var page = _graph.GetNode(pageId);
        if (page == null || page.Type != NodeType.RazorPage) return null;

        var model = GetNeighbors(pageId, EdgeType.PageServedBy)
            .Select(n => n.Target)
            .FirstOrDefault(n => n.Type == NodeType.PageModel);

        // InjectedInto edges point service -> consumer, so services are predecessors.
        var services = model != null
            ? GetPredecessors(model.Id, EdgeType.InjectedInto).Select(n => n.Source).ToList()
            : new List<GraphNode>();

        var viewModel = page.GetProperty<string>("modelType");
        GraphNode? vmNode = null;
        if (!string.IsNullOrWhiteSpace(viewModel))
            vmNode = FindNodes(NodeType.ViewModel, viewModel).FirstOrDefault()
                  ?? FindNodes(NodeType.Class, viewModel).FirstOrDefault();

        return new PageContext(page, model, vmNode, services);
    }

    /// <summary>
    /// Detect anti-patterns: server-prepared data consumed by client JS.
    /// Returns nodes where ViewData/Model properties are set but read by JS file nodes.
    /// </summary>
    public IEnumerable<(GraphNode ServerNode, GraphNode JsNode, GraphEdge Edge)> FindServerToJsMismatches()
    {
        // Find all JS files that read ViewData or reference model properties
        foreach (var js in _graph.NodesOfType(NodeType.JavaScriptFile))
        {
            foreach (var edge in _graph.Incoming(js.Id))
            {
                if (edge.Type == EdgeType.ViewDataReadBy || edge.Type == EdgeType.Reads)
                {
                    var source = _graph.GetNode(edge.FromId);
                    if (source != null) yield return (source, js, edge);
                }
            }
        }
    }
}

/// <summary>
/// Bundled context for a Razor page and its backing infrastructure.
/// </summary>
public sealed record PageContext(
    GraphNode Page,
    GraphNode? PageModel,
    GraphNode? ViewModel,
    IReadOnlyList<GraphNode> InjectedServices);
