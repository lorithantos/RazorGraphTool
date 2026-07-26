namespace RazorGraph.Core.Graph;

/// <summary>
/// A node in the code graph representing any discoverable concept:
/// RazorPage, PageModel, Service, PartialView, Method, Property, etc.
/// </summary>
public sealed class GraphNode
{
    public required string Id { get; init; }
    public required NodeType Type { get; init; }
    public required string Name { get; init; }
    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }

    /// <summary>
    /// Type-agnostic properties. Keys are well-known per NodeType.
    /// </summary>
    public Dictionary<string, object> Properties { get; } = new();

    /// <summary>
    /// Labels for quick categorization (e.g., "admin", "api", "shared").
    /// </summary>
    public List<string> Labels { get; } = new();

    public T? GetProperty<T>(string key) =>
        Properties.TryGetValue(key, out var value) && value is T t ? t : default;

    public void SetProperty<T>(string key, T value) => Properties[key] = value!;
}
