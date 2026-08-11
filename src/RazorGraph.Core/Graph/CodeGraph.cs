namespace RazorGraph.Core.Graph;

/// <summary>
/// The in-memory graph container. Thread-safe for reads; single-writer assumed during build.
/// </summary>
public sealed class CodeGraph
{
    private readonly Dictionary<string, GraphNode> _nodes = new();
    private readonly List<GraphEdge> _edges = new();
    private readonly Dictionary<string, List<GraphEdge>> _outgoing = new();
    private readonly Dictionary<string, List<GraphEdge>> _incoming = new();

    public IReadOnlyCollection<GraphNode> Nodes => _nodes.Values;
    public IReadOnlyCollection<GraphEdge> Edges => _edges.AsReadOnly();

    /// <summary>
    /// Format versions newer than this build's that data in this graph was read
    /// from. Empty for a graph built from source, or loaded from a file at or
    /// below this build's version.
    ///
    /// Carried on the graph rather than recomputed at save time because it is
    /// not derivable: a newer format can add a property to a node kind we do
    /// know, leaving nothing locally strange to notice. Drop this and foreign
    /// data becomes indistinguishable from data this build understands, which is
    /// the exact confusion the format stamp exists to prevent.
    /// </summary>
    public SortedSet<string> ForeignFormatVersions { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Node kinds present in this graph that this build has no
    /// <see cref="NodeType"/> for. What a caller may select by name but will not
    /// find in the enum — so an error message can name them instead of implying
    /// they do not exist.
    /// </summary>
    public IReadOnlyList<string> ForeignNodeKinds => DistinctKinds(_nodes.Values.Select(n => n.ForeignType));

    /// <summary>Edge kinds present that this build has no <see cref="EdgeType"/> for.</summary>
    public IReadOnlyList<string> ForeignEdgeKinds => DistinctKinds(_edges.Select(e => e.ForeignType));

    private static IReadOnlyList<string> DistinctKinds(IEnumerable<string?> kinds) =>
        kinds.Where(k => k is not null).Select(k => k!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public void AddNode(GraphNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        _nodes[node.Id] = node;
        // Re-adding an id must not reset adjacency — edges added before the upsert stay reachable.
        if (!_outgoing.ContainsKey(node.Id)) _outgoing[node.Id] = new List<GraphEdge>();
        if (!_incoming.ContainsKey(node.Id)) _incoming[node.Id] = new List<GraphEdge>();
    }

    public void AddEdge(GraphEdge edge)
    {
        ArgumentNullException.ThrowIfNull(edge);
        _edges.Add(edge);

        if (!_outgoing.ContainsKey(edge.FromId))
            _outgoing[edge.FromId] = new List<GraphEdge>();
        _outgoing[edge.FromId].Add(edge);

        if (!_incoming.ContainsKey(edge.ToId))
            _incoming[edge.ToId] = new List<GraphEdge>();
        _incoming[edge.ToId].Add(edge);
    }

    public GraphNode? GetNode(string id) => _nodes.TryGetValue(id, out var n) ? n : null;

    public bool HasNode(string id) => _nodes.ContainsKey(id);

    public IReadOnlyList<GraphEdge> Outgoing(string nodeId) =>
        _outgoing.TryGetValue(nodeId, out var list) ? list : Array.Empty<GraphEdge>();

    public IReadOnlyList<GraphEdge> Incoming(string nodeId) =>
        _incoming.TryGetValue(nodeId, out var list) ? list : Array.Empty<GraphEdge>();

    public IEnumerable<GraphNode> NodesOfType(NodeType type) =>
        _nodes.Values.Where(n => n.Type == type);

    public IEnumerable<GraphNode> NodesWithLabel(string label) =>
        _nodes.Values.Where(n => n.Labels.Contains(label));

    /// <summary>
    /// Neighbours of a node in the requested direction, paired with the edge that
    /// reaches them. Incoming traversal walks an edge backwards, so the neighbour
    /// is its FromId — the edge object itself is unchanged and still reports the
    /// authored direction to the caller.
    /// </summary>
    private IEnumerable<(GraphEdge Edge, string NeighborId)> Adjacent(string nodeId, TraversalDirection direction)
    {
        if (direction is TraversalDirection.Outgoing or TraversalDirection.Both)
        {
            foreach (var edge in Outgoing(nodeId)) yield return (edge, edge.ToId);
        }

        if (direction is TraversalDirection.Incoming or TraversalDirection.Both)
        {
            foreach (var edge in Incoming(nodeId)) yield return (edge, edge.FromId);
        }
    }

    /// <summary>
    /// Follow edges of given types from a starting node, up to maxDepth.
    /// </summary>
    /// <param name="transparentEdges">
    /// Edge types that are followed without consuming depth. Structural edges
    /// (a class containing its methods) describe the same place in the code
    /// rather than a step through it; charging them a hop meant a depth-3 trace
    /// from a PageModel spent its whole budget getting to the methods and
    /// reported none of the calls they make.
    /// </param>
    public IEnumerable<(GraphNode Node, GraphEdge Edge, int Depth)> Traverse(
        string startId,
        IReadOnlySet<EdgeType>? edgeFilter = null,
        int maxDepth = 3,
        TraversalDirection direction = TraversalDirection.Outgoing,
        IReadOnlySet<EdgeType>? transparentEdges = null)
    {
        var visited = new HashSet<string> { startId };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((startId, 0));

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();

            foreach (var (edge, neighborId) in Adjacent(currentId, direction))
            {
                if (edgeFilter != null && !edgeFilter.Contains(edge.Type)) continue;

                var isTransparent = transparentEdges != null && transparentEdges.Contains(edge.Type);
                // Depth is checked per-edge rather than per-node so a transparent
                // edge can still expand a node sitting at the depth limit.
                if (!isTransparent && depth >= maxDepth) continue;

                if (!visited.Add(neighborId)) continue;

                var node = GetNode(neighborId);
                if (node == null) continue;

                var nextDepth = isTransparent ? depth : depth + 1;
                yield return (node, edge, nextDepth);
                queue.Enqueue((neighborId, nextDepth));
            }
        }
    }

    /// <summary>
    /// Find paths between two nodes (simple BFS, returns first found).
    /// </summary>
    public IReadOnlyList<GraphEdge>? FindPath(
        string fromId,
        string toId,
        IReadOnlySet<EdgeType>? edgeFilter = null,
        TraversalDirection direction = TraversalDirection.Outgoing)
    {
        if (fromId == toId) return Array.Empty<GraphEdge>();

        var queue = new Queue<(List<GraphEdge> Path, string Head)>();
        var visited = new HashSet<string> { fromId };

        foreach (var (edge, neighborId) in Adjacent(fromId, direction))
        {
            if (edgeFilter != null && !edgeFilter.Contains(edge.Type)) continue;
            queue.Enqueue((new List<GraphEdge> { edge }, neighborId));
        }

        while (queue.Count > 0)
        {
            var (path, head) = queue.Dequeue();

            if (head == toId) return path;
            if (!visited.Add(head)) continue;

            foreach (var (next, neighborId) in Adjacent(head, direction))
            {
                if (edgeFilter != null && !edgeFilter.Contains(next.Type)) continue;
                queue.Enqueue((new List<GraphEdge>(path) { next }, neighborId));
            }
        }

        return null;
    }
}
