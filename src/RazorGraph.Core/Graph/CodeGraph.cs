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
    /// Follow edges of given types from a starting node, up to maxDepth.
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge, int Depth)> Traverse(
        string startId,
        IReadOnlySet<EdgeType>? edgeFilter = null,
        int maxDepth = 3)
    {
        var visited = new HashSet<string>();
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((startId, 0));
        visited.Add(startId);

        while (queue.Count > 0)
        {
            var (currentId, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (var edge in Outgoing(currentId))
            {
                if (edgeFilter != null && !edgeFilter.Contains(edge.Type)) continue;
                if (!visited.Add(edge.ToId)) continue;

                var node = GetNode(edge.ToId);
                if (node != null)
                {
                    yield return (node, edge, depth + 1);
                    queue.Enqueue((edge.ToId, depth + 1));
                }
            }
        }
    }

    /// <summary>
    /// Find paths between two nodes (simple BFS, returns first found).
    /// </summary>
    public IReadOnlyList<GraphEdge>? FindPath(string fromId, string toId, IReadOnlySet<EdgeType>? edgeFilter = null)
    {
        var queue = new Queue<List<GraphEdge>>();
        var visited = new HashSet<string> { fromId };

        foreach (var edge in Outgoing(fromId))
        {
            if (edgeFilter != null && !edgeFilter.Contains(edge.Type)) continue;
            queue.Enqueue(new List<GraphEdge> { edge });
        }

        while (queue.Count > 0)
        {
            var path = queue.Dequeue();
            var last = path[^1];

            if (last.ToId == toId) return path;
            if (!visited.Add(last.ToId)) continue;

            foreach (var next in Outgoing(last.ToId))
            {
                if (edgeFilter != null && !edgeFilter.Contains(next.Type)) continue;
                var newPath = new List<GraphEdge>(path) { next };
                queue.Enqueue(newPath);
            }
        }

        return null;
    }
}
