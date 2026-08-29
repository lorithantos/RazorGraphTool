namespace RazorGraph.Mcp;

using System.Text.Json;
using System.Text.Json.Serialization;
using RazorGraph.Core.Graph;

/// <summary>
/// Result conventions for the MCP tool surface. All results are compact JSON —
/// the consumer is a model, not a terminal. Search-style tools return the
/// envelope { returned, totalMatches, truncated, ... } so callers can detect
/// capped results.
/// </summary>
internal static class ToolResponses
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    /// <summary>
    /// <c>fileWrittenSince</c> is emitted only when it is true, and only when a
    /// freshness probe was supplied: absent means "not asked or not knowable",
    /// never "verified unchanged". True says the file behind this node has been
    /// WRITTEN since the graph was built — the node's file, not its meaning, and
    /// not the files it points at.
    /// </summary>
    internal static object NodeSummary(GraphNode n, GraphFreshness? freshness = null) => new
    {
        id = n.Id,
        type = n.DisplayType,
        name = n.Name,
        project = n.GetProperty<string>("project"),
        filePath = n.FilePath,
        line = n.LineStart,
        fileWrittenSince = Stale(n, freshness)
    };

    internal static object NodeDetail(GraphNode n, GraphFreshness? freshness = null) => new
    {
        id = n.Id,
        type = n.DisplayType,
        name = n.Name,
        filePath = n.FilePath,
        lineStart = n.LineStart,
        lineEnd = n.LineEnd,
        fileWrittenSince = Stale(n, freshness),
        properties = n.Properties.Count > 0 ? n.Properties : null,
        labels = n.Labels.Count > 0 ? n.Labels : null
    };

    // Null rather than false when unchanged, so the field disappears under
    // WhenWritingNull: a flag on every clean node in a 500-node result is noise
    // that trains the reader to skip the one that matters.
    private static bool? Stale(GraphNode n, GraphFreshness? freshness) =>
        freshness?.WrittenSinceBuild(n) == true ? true : null;
}
