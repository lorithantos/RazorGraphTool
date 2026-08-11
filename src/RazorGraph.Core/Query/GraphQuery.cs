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
    /// Find nodes by kind NAME rather than by enum member, so a kind this build
    /// has no <see cref="NodeType"/> for is still selectable — a graph written by
    /// a newer version or a third-party extractor stays queryable by whoever is
    /// holding it, without waiting for a build that knows the kind.
    /// </summary>
    /// <remarks>
    /// Matching is on <see cref="GraphNode.DisplayType"/>, which is the same
    /// string every report and tool response shows, so a kind a caller read out
    /// of a result is a kind they can paste straight back in.
    /// </remarks>
    public IEnumerable<GraphNode> FindNodes(string kind, string? nameContains = null)
    {
        var query = _graph.Nodes.Where(n => string.Equals(n.DisplayType, kind, StringComparison.OrdinalIgnoreCase));
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
    /// Edges that carry data or control between two places in the code.
    /// Contains is in the set but is <see cref="StructuralEdges">transparent</see>:
    /// call edges hang off Method nodes, so a trace that cannot descend from a
    /// class into its own methods reports nothing for every class-level node —
    /// which is exactly the node type callers start from.
    /// </summary>
    private static readonly HashSet<EdgeType> DataFlowEdges = new()
    {
        EdgeType.Reads, EdgeType.Writes, EdgeType.BindsTo,
        EdgeType.Calls, EdgeType.InjectedInto, EdgeType.ReturnsView,
        EdgeType.Contains
    };

    /// <summary>Followed without consuming depth; see CodeGraph.Traverse.</summary>
    private static readonly HashSet<EdgeType> StructuralEdges = new() { EdgeType.Contains };

    /// <summary>
    /// Trace data flow: find all nodes reachable from start via Reads/Writes/BindsTo/Calls edges,
    /// descending through containment for free.
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge, int Depth)> TraceDataFlow(
        string startId,
        int maxDepth = 3,
        TraversalDirection direction = TraversalDirection.Outgoing) =>
        _graph.Traverse(startId, DataFlowEdges, maxDepth, direction, StructuralEdges);

    /// <summary>
    /// Test methods that exercise the given production method, nearest first.
    /// Covers edges point test -> code, so this is an incoming lookup.
    /// </summary>
    public IEnumerable<(GraphNode Test, int Depth)> GetCoveringTests(string methodId)
    {
        RequireTestMethods("covering-tests");
        return _graph.Incoming(methodId)
            .Where(e => e.Type == EdgeType.Covers)
            .Select(e => (Test: _graph.GetNode(e.FromId), Depth: e.GetProperty<int>("depth")))
            .Where(t => t.Test != null)
            .Select(t => (t.Test!, t.Depth))
            .OrderBy(t => t.Depth);
    }

    /// <summary>
    /// Production methods a given test exercises, nearest first.
    /// </summary>
    public IEnumerable<(GraphNode Method, int Depth)> GetCoveredMethods(string testId)
    {
        RequireTestMethods("covered-methods");
        return _graph.Outgoing(testId)
            .Where(e => e.Type == EdgeType.Covers)
            .Select(e => (Method: _graph.GetNode(e.ToId), Depth: e.GetProperty<int>("depth")))
            .Where(t => t.Method != null)
            .Select(t => (t.Method!, t.Depth))
            .OrderBy(t => t.Depth);
    }

    /// <summary>
    /// Methods with no test reaching them. Restricted to a project when given,
    /// because "untested" only means something inside a production assembly —
    /// the test project's own helpers are not the subject of the question.
    ///
    /// Bodiless declarations are excluded. An interface member never carries a
    /// Covers edge — calls from a test bind to the implementation, not the
    /// declaration — so including them would report every interface in the
    /// solution as untested and make the whole report untrustworthy.
    /// </summary>
    public IEnumerable<GraphNode> FindUncoveredMethods(string? project = null)
    {
        RequireTestMethods("uncovered-methods");
        return _graph.NodesOfType(NodeType.Method)
            .Where(m => !m.GetProperty<bool>("isTest"))
            .Where(m => !m.GetProperty<bool>("isAbstract"))
            .Where(m => project == null ||
                        string.Equals(m.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
            .Where(m => !_graph.Incoming(m.Id).Any(e => e.Type == EdgeType.Covers));
    }

    /// <summary>
    /// The relevance map behind a research selection: focus nodes score 1.0,
    /// every node reachable within maxDepth scores 1/(1+depth), and when
    /// multiple focus nodes reach the same node the best (nearest) score
    /// wins. Unknown focus ids come back in MissingFocusIds rather than
    /// throwing: whether a missing focus is a warning (CLI) or a hard error
    /// (MCP) is front-end policy. The scoring rule itself must not fork per
    /// front end — it lived as two copies once, and they had already diverged
    /// on direction support when this method un-forked them.
    /// </summary>
    public (Dictionary<string, double> Relevance, List<string> MissingFocusIds) ComputeRelevance(
        IEnumerable<string> focusIds,
        int maxDepth,
        TraversalDirection direction = TraversalDirection.Outgoing)
    {
        var relevance = new Dictionary<string, double>();
        var missing = new List<string>();
        foreach (var id in focusIds.Distinct())
        {
            if (!_graph.HasNode(id)) { missing.Add(id); continue; }
            relevance[id] = 1.0;
        }

        foreach (var id in relevance.Keys.ToList())
        {
            foreach (var (node, _, depth) in _graph.Traverse(id, edgeFilter: null, maxDepth: maxDepth, direction: direction))
            {
                var score = 1.0 / (1 + depth);
                if (!relevance.TryGetValue(node.Id, out var existing) || score > existing)
                    relevance[node.Id] = score;
            }
        }

        return (relevance, missing);
    }

    /// <summary>
    /// A coverage question against a graph containing no test methods has no
    /// answer, not an empty one: every method would read as uncovered, which
    /// is the absence-of-data-as-finding trap. Refusing loudly is the same
    /// contract as the escapes guard in the MCP layer. Fires for graphs built
    /// single-project or with tests excluded (--no-tests / excludeTests).
    /// </summary>
    private void RequireTestMethods(string operation)
    {
        if (!_graph.NodesOfType(NodeType.Method).Any(m => m.GetProperty<bool>("isTest")))
            throw new InvalidOperationException(
                $"{operation} needs a graph containing test methods, and this graph has none — "
                + "built from a single project, or with tests excluded? Every method would read "
                + "as uncovered, so refusing to answer. Rebuild with build-solution, without --no-tests.");
    }

    /// <summary>
    /// Methods whose body nests control flow at least minDepth levels deep —
    /// the christmas-tree report, deepest first. bodyDepth is stamped at
    /// extraction time (see BodyGraphExtractor.NestingDepth); methods without
    /// the property are flat or bodiless and never match a minDepth above 0.
    /// </summary>
    public IEnumerable<GraphNode> FindDeepMethods(int minDepth, string? project = null) =>
        _graph.NodesOfType(NodeType.Method)
            .Where(m => m.GetProperty<int>("bodyDepth") >= minDepth)
            .Where(m => project == null ||
                        string.Equals(m.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.GetProperty<int>("bodyDepth"));

    /// <summary>
    /// Exception escapes, shallowest first: Escapes edges (precomputed at
    /// build time) resolved to their thrower and entry-point nodes. Every
    /// filter narrows; all default to everything. entryPointKind matches the
    /// entry node's stamp exactly; exceptionTypeContains is a case-insensitive
    /// substring of the escaping type.
    /// </summary>
    public IEnumerable<(GraphNode Thrower, GraphNode EntryPoint, GraphEdge Edge)> FindEscapingExceptions(
        string? entryPointKind = null,
        string? exceptionTypeContains = null,
        string? project = null,
        string? entryPointId = null) =>
        _graph.Edges
            .Where(e => e.Type == EdgeType.Escapes)
            .Where(e => entryPointId == null || e.ToId == entryPointId)
            .Where(e => exceptionTypeContains == null ||
                        (e.GetProperty<string>("exceptionType") ?? "")
                            .Contains(exceptionTypeContains, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Thrower: _graph.GetNode(e.FromId), EntryPoint: _graph.GetNode(e.ToId), Edge: e))
            .Where(t => t.Thrower != null && t.EntryPoint != null)
            .Where(t => entryPointKind == null ||
                        string.Equals(t.EntryPoint!.GetProperty<string>("entryPointKind"), entryPointKind,
                            StringComparison.Ordinal))
            .Where(t => project == null ||
                        string.Equals(t.EntryPoint!.GetProperty<string>("project"), project,
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Edge.GetProperty<int>("depth"))
            .ThenBy(t => t.EntryPoint!.Id, StringComparer.Ordinal)
            .ThenBy(t => t.Edge.GetProperty<string>("exceptionType"), StringComparer.Ordinal)
            .Select(t => (t.Thrower!, t.EntryPoint!, t.Edge));

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
        // JS files consuming server-prepared state by name: ViewData/data-*
        // keys, model reads, and element ids reached by literal selectors.
        foreach (var js in _graph.NodesOfType(NodeType.JavaScriptFile))
        {
            foreach (var edge in _graph.Incoming(js.Id))
            {
                if (edge.Type is EdgeType.ViewDataReadBy or EdgeType.Reads or EdgeType.DomSelectedBy)
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
