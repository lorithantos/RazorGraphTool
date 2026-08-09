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

    internal static object NodeSummary(GraphNode n) => new
    {
        id = n.Id,
        type = n.Type.ToString(),
        name = n.Name,
        project = n.GetProperty<string>("project"),
        filePath = n.FilePath,
        line = n.LineStart
    };

    internal static object NodeDetail(GraphNode n) => new
    {
        id = n.Id,
        type = n.Type.ToString(),
        name = n.Name,
        filePath = n.FilePath,
        lineStart = n.LineStart,
        lineEnd = n.LineEnd,
        properties = n.Properties.Count > 0 ? n.Properties : null,
        labels = n.Labels.Count > 0 ? n.Labels : null
    };
}
