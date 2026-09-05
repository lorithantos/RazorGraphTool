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

    /// <summary>
    /// <c>lineDocs</c> is the first line of the declaration's XML documentation
    /// comment, absent when it has none. With <c>lineEnd</c> it makes the node
    /// say what to read rather than only where to look: <c>lineDocs ??
    /// lineStart</c> through <c>lineEnd</c> is the declaration and its
    /// documentation, so a caller opens the exact chunk instead of a line
    /// number and a guess.
    /// </summary>
    internal static object NodeDetail(GraphNode n, GraphFreshness? freshness = null) => new
    {
        id = n.Id,
        type = n.DisplayType,
        name = n.Name,
        filePath = n.FilePath,
        lineDocs = n.LineDocs,
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

    /// <summary>
    /// Why an absent <c>fileWrittenSince</c> proves nothing on this graph, or
    /// null when it proves what a reader expects.
    /// </summary>
    /// <remarks>
    /// Emitting the flag only when true is right — a marker on every clean node
    /// in a 500-node result trains the reader to skip the one that matters —
    /// but it makes absence carry two meanings: "its file is unchanged" and
    /// "there is no build stamp, so nothing was computed". The first is
    /// evidence and the second is silence, and a reader cannot tell them apart
    /// from the node alone. So the graph says once, per response, which kind of
    /// absence this is.
    /// <para>
    /// This matters most for the line span. <c>lineStart</c>, <c>lineEnd</c> and
    /// <c>lineDocs</c> are only true of the source as it stood at build time, so
    /// "the file behind this node has not been written since" is exactly the
    /// warrant for reading them — and on a graph with no stamp there is no
    /// warrant, only a quiet field.
    /// </para>
    /// </remarks>
    internal static string? FreshnessCaveat(CodeGraph graph) =>
        graph.BuiltAt is null
            ? "This graph carries no build stamp, so fileWrittenSince was not computed: "
              + "its absence on a node here means NOTHING, not 'file unchanged'. Line spans "
              + "are unwarranted against the current file. Rebuild to restore the check."
            : null;
}
