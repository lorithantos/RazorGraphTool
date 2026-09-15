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
    /// <c>docsStart</c> is the first line of the declaration's XML documentation
    /// comment, absent when it has none. With <c>lineEnd</c> it makes the node
    /// say what to read rather than only where to look: <c>docsStart ??
    /// lineStart</c> through <c>lineEnd</c> is the declaration and its
    /// documentation, so a caller opens the exact chunk instead of a line
    /// number and a guess.
    /// </summary>
    /// <remarks>
    /// Scalar properties come back inline and list-valued ones come back as
    /// counts, expandable by name. That split is measured rather than guessed:
    /// on a 2,098-node graph the scalars total 245KB, about 117 bytes a node,
    /// which is cheap and is exactly what orients a reader — is this public,
    /// static, abstract, what does it return. The lists are 37KB concentrated on
    /// 229 nodes, and they own every fat payload; the worst is a test class
    /// whose method-name list is 2,948 bytes and duplicates the Contains edges
    /// that same node already reports. So stripping the scalars would buy
    /// nothing and cost a round trip, while sending the lists unasked is the
    /// same mistake the edge summary exists to stop, one level in.
    /// </remarks>
    internal static object NodeDetail(GraphNode n, GraphFreshness? freshness = null, string? expand = null)
    {
        var scalars = new Dictionary<string, object>(StringComparer.Ordinal);
        var collections = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (var (key, value) in n.Properties)
        {
            if (!IsList(value))
            {
                scalars[key] = value;
                continue;
            }

            var items = ((System.Collections.IEnumerable)value).Cast<object>().ToList();
            var wanted = expand is not null
                && (string.Equals(expand, "all", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(expand, key, StringComparison.OrdinalIgnoreCase));

            collections[key] = new
            {
                count = items.Count,
                expanded = wanted,
                values = wanted ? (object?)items : null
            };
        }

        return new
        {
            id = n.Id,
            type = n.DisplayType,
            name = n.Name,
            filePath = n.FilePath,
            docsStart = n.DocsStart,
            lineStart = n.LineStart,
            lineEnd = n.LineEnd,
            fileWrittenSince = Stale(n, freshness),
            properties = scalars.Count > 0 ? scalars : null,
            collections = collections.Count > 0 ? collections : null,
            labels = n.Labels.Count > 0 ? n.Labels : null
        };
    }

    /// <summary>
    /// One page of an ordered result, and how to ask for the next.
    /// </summary>
    /// <remarks>
    /// <c>nextOffset</c> is present only when there is more, so its presence is
    /// the "keep going" signal and its absence is "that was the end" — a caller
    /// never has to compare two numbers to find out whether it finished.
    /// <para>
    /// Offset rather than an opaque cursor, because a built graph does not
    /// change under the reader: the rows are the same on every call, so the
    /// only thing a cursor would buy is protection against reordering that
    /// cannot happen. It does require the underlying order to be TOTAL, which
    /// is why the coverage queries order by depth AND id — an offset into a
    /// partially ordered result silently repeats and skips rows.
    /// </para>
    /// </remarks>
    internal readonly record struct PageWindow(
        int Returned, int TotalMatches, int Offset, bool Truncated, int? NextOffset);

    internal static (PageWindow Window, List<TItem> Items) Paginate<TSource, TItem>(
        IReadOnlyList<TSource> all, int offset, int limit, Func<TSource, TItem> project)
    {
        var from = Math.Clamp(offset, 0, all.Count);
        var take = Math.Clamp(limit, 1, 1000);
        var page = all.Skip(from).Take(take).Select(project).ToList();
        var end = from + page.Count;
        var more = end < all.Count;

        return (new PageWindow(page.Count, all.Count, from, more, more ? end : null), page);
    }

    /// <summary>The list-valued property names on a node — what <c>fields</c> can expand.</summary>
    internal static List<string> ListPropertyNames(GraphNode n) =>
        n.Properties.Where(p => IsList(p.Value))
                    .Select(p => p.Key)
                    .OrderBy(k => k, StringComparer.Ordinal)
                    .ToList();

    // A string is IEnumerable and is not a list in any sense a reader means.
    private static bool IsList(object? value) =>
        value is System.Collections.IEnumerable and not string;

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
    /// <c>docsStart</c> are only true of the source as it stood at build time, so
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
