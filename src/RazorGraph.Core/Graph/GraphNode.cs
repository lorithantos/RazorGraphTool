namespace RazorGraph.Core.Graph;

/// <summary>
/// A node in the code graph representing any discoverable concept:
/// RazorPage, PageModel, Service, PartialView, Method, Property, etc.
/// </summary>
public sealed class GraphNode
{
    public required string Id { get; init; }
    public required NodeType Type { get; init; }

    /// <summary>
    /// The node kind exactly as it was written, when it is a name this build has
    /// no <see cref="NodeType"/> for — a newer format's addition, or a
    /// third-party extractor's own vocabulary. <see cref="Type"/> is
    /// <see cref="NodeType.Unknown"/> in that case, and this is what survives a
    /// save so the foreign graph is not silently rewritten into ours.
    /// Null for every node this build understands, including a genuine
    /// <see cref="NodeType.Unknown"/>.
    /// </summary>
    public string? ForeignType { get; init; }

    /// <summary>
    /// The kind to show a user or a tool response: the foreign name when there is
    /// one, otherwise the enum. Preserving a kind in storage and then rendering it
    /// as "Unknown" everywhere would discard it at exactly the moment it is being
    /// read, which is the only moment it matters.
    /// </summary>
    public string DisplayType => ForeignType ?? Type.ToString();

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
