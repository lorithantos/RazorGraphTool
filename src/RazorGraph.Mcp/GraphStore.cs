namespace RazorGraph.Mcp;

using System.Collections.Concurrent;
using ModelContextProtocol;
using RazorGraph.Core.Graph;

/// <summary>
/// Every graph the server has built or loaded, keyed by a short id.
///
/// The previous design held exactly one graph, so a second build_graph destroyed
/// the first. That is not only inconvenient — it makes whole classes of question
/// unanswerable, because comparing two graphs (before/after a refactor, two
/// projects side by side) requires having two. Concurrency follows from the same
/// change: with one slot per id, two builds can run at once without racing for
/// the same field.
/// </summary>
public sealed class GraphStore
{
    private readonly ConcurrentDictionary<string, GraphEntry> _graphs =
        new(StringComparer.OrdinalIgnoreCase);

    // Only the "which id did the caller mean by default" pointer needs guarding;
    // the graphs themselves live in a concurrent map.
    private readonly object _currentGate = new();
    private string? _currentId;

    public sealed record GraphEntry(string Id, CodeGraph Graph, string Source, DateTimeOffset LoadedAt);

    /// <summary>
    /// Register a graph and make it the default for calls that omit graphId.
    /// A requested id replaces any graph already under it — rebuilding after an
    /// edit should update in place rather than accumulate near-duplicates.
    /// </summary>
    public GraphEntry Add(CodeGraph graph, string source, string? requestedId)
    {
        var id = string.IsNullOrWhiteSpace(requestedId)
            ? NextAvailableId(DeriveId(source))
            : requestedId.Trim();

        var entry = new GraphEntry(id, graph, source, DateTimeOffset.UtcNow);
        _graphs[id] = entry;

        lock (_currentGate) _currentId = id;
        return entry;
    }

    /// <summary>
    /// The named graph, or the most recently added one when no id is given.
    /// </summary>
    public GraphEntry Require(string? graphId)
    {
        if (!string.IsNullOrWhiteSpace(graphId))
        {
            if (_graphs.TryGetValue(graphId.Trim(), out var found)) return found;
            throw new McpException(
                $"No graph with id '{graphId}'. Loaded: {DescribeLoaded()}. " +
                "Use list_graphs to see them, or build_graph / load_graph to add one.");
        }

        string? current;
        lock (_currentGate) current = _currentId;

        if (current != null && _graphs.TryGetValue(current, out var entry)) return entry;

        throw new McpException(
            "No graph is loaded. Call build_graph (a .csproj), build_solution (a whole .sln), " +
            "or load_graph (a graph JSON file) first.");
    }

    public IReadOnlyList<GraphEntry> List() =>
        _graphs.Values.OrderBy(e => e.LoadedAt).ToList();

    public string? CurrentId
    {
        get { lock (_currentGate) return _currentId; }
    }

    /// <summary>
    /// Forget a graph. Dropping the default leaves the server with no default
    /// rather than silently promoting an arbitrary other graph.
    /// </summary>
    public bool Remove(string graphId)
    {
        if (!_graphs.TryRemove(graphId.Trim(), out _)) return false;

        lock (_currentGate)
        {
            if (string.Equals(_currentId, graphId.Trim(), StringComparison.OrdinalIgnoreCase))
                _currentId = null;
        }
        return true;
    }

    private string DescribeLoaded() =>
        _graphs.IsEmpty ? "(none)" : string.Join(", ", _graphs.Keys.OrderBy(k => k));

    /// <summary>
    /// A readable id from the source path: the file name without extension, so a
    /// graph of ImageSelectorTest.sln is addressable as "ImageSelectorTest".
    /// </summary>
    private static string DeriveId(string source)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        if (string.IsNullOrWhiteSpace(name)) return "graph";

        // A graph JSON saved as "Foo.graph.json" should come back as "Foo", not "Foo.graph".
        if (name.EndsWith(".graph", StringComparison.OrdinalIgnoreCase))
            name = name[..^".graph".Length];

        return string.IsNullOrWhiteSpace(name) ? "graph" : name;
    }

    private string NextAvailableId(string baseId)
    {
        if (!_graphs.ContainsKey(baseId)) return baseId;

        for (var i = 2; ; i++)
        {
            var candidate = $"{baseId}-{i}";
            if (!_graphs.ContainsKey(candidate)) return candidate;
        }
    }
}
