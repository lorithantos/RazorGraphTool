namespace RazorGraph.Core.Graph;

/// <summary>
/// A directed edge between two graph nodes.
/// </summary>
public sealed class GraphEdge
{
    public required string FromId { get; init; }
    public required string ToId { get; init; }
    public required EdgeType Type { get; init; }

    /// <summary>
    /// The edge kind exactly as it was written, when it is a name this build has
    /// no <see cref="EdgeType"/> for. Same contract as
    /// <see cref="GraphNode.ForeignType"/>: <see cref="Type"/> is
    /// <see cref="EdgeType.Unknown"/>, the original name survives a save, and no
    /// query in this build reasons about it.
    /// </summary>
    public string? ForeignType { get; init; }

    /// <summary>
    /// The kind to show a user or a tool response. See
    /// <see cref="GraphNode.DisplayType"/>.
    /// </summary>
    public string DisplayType => ForeignType ?? Type.ToString();

    /// <summary>
    /// Edge-specific metadata (e.g., binding expression, HTTP verb, view name).
    /// </summary>
    public Dictionary<string, object> Properties { get; } = new();

    public T? GetProperty<T>(string key) =>
        Properties.TryGetValue(key, out var value) && value is T t ? t : default;
}
