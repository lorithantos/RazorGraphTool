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
    /// Edge-specific metadata (e.g., binding expression, HTTP verb, view name).
    /// </summary>
    public Dictionary<string, object> Properties { get; } = new();

    public T? GetProperty<T>(string key) =>
        Properties.TryGetValue(key, out var value) && value is T t ? t : default;
}
