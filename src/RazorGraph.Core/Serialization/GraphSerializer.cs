namespace RazorGraph.Core.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using RazorGraph.Core.Graph;

/// <summary>
/// A loaded graph together with what its <c>formatVersion</c> stamp said.
/// <see cref="GraphFormatAssessment.Caveat"/> is null on a clean same-version
/// read and otherwise carries text meant to reach the user.
/// </summary>
public sealed record GraphReadResult(CodeGraph Graph, GraphFormatAssessment Format);

/// <summary>
/// Serializes and deserializes CodeGraph to/from JSON.
/// Produces a format suitable for research.json ingestion and LLM tool queries.
/// </summary>
public static class GraphSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static string ToJson(CodeGraph graph)
    {
        var dto = new GraphDto
        {
            FormatVersion = GraphFormat.Current.ToString(),
            ForeignData = DescribeForeignData(graph),
            Nodes = graph.Nodes.Select(ToNodeDto).ToList(),
            Edges = graph.Edges.Select(e => new EdgeDto
            {
                From = e.FromId,
                To = e.ToId,
                Type = TypeName(e.Type, e.ForeignType),
                Properties = e.Properties
            }).ToList()
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    private static NodeDto ToNodeDto(GraphNode n) => new()
    {
        Id = n.Id,
        Type = TypeName(n.Type, n.ForeignType),
        Name = n.Name,
        FilePath = n.FilePath,
        LineStart = n.LineStart,
        LineEnd = n.LineEnd,
        Properties = n.Properties,
        Labels = n.Labels
    };

    /// <summary>
    /// A kind's wire name: the camelCase enum name for vocabulary this build
    /// models, and otherwise the foreign name verbatim. Verbatim matters —
    /// re-casing a third party's name is a silent rewrite of their graph, and the
    /// round-trip is only lossless if what we write back is what we read.
    /// </summary>
    private static string TypeName<TEnum>(TEnum type, string? foreignType) where TEnum : struct, Enum =>
        foreignType ?? JsonNamingPolicy.CamelCase.ConvertName(type.ToString()!);

    /// <summary>
    /// What in this graph the writer could not model, or null when everything in
    /// it belongs to this format version.
    /// </summary>
    private static ForeignDataDto? DescribeForeignData(CodeGraph graph)
    {
        var foreignNodes = graph.Nodes.Where(n => n.ForeignType is not null).ToList();
        var foreignEdges = graph.Edges.Where(e => e.ForeignType is not null).ToList();
        var fromVersions = graph.ForeignFormatVersions.ToList();

        if (foreignNodes.Count == 0 && foreignEdges.Count == 0 && fromVersions.Count == 0) return null;

        return new ForeignDataDto
        {
            Comment = GraphFormat.ForeignDataComment(fromVersions),
            FromVersions = fromVersions.Count > 0 ? fromVersions : null,
            NodeTypes = Distinct(foreignNodes.Select(n => n.ForeignType!)),
            EdgeTypes = Distinct(foreignEdges.Select(e => e.ForeignType!)),
            Nodes = foreignNodes.Count,
            Edges = foreignEdges.Count
        };
    }

    private static List<string>? Distinct(IEnumerable<string> names)
    {
        var list = names.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList();
        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Load a graph and report the format version it carried. Prefer this over
    /// <see cref="FromJson"/> anywhere the result is shown to a user: the whole
    /// point of the stamp is that a version mismatch gets said out loud, and a
    /// caller that only takes the graph has thrown that report away.
    /// </summary>
    public static GraphReadResult Read(string json)
    {
        var dto = JsonSerializer.Deserialize<GraphDto>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize graph JSON.");

        var assessment = GraphFormat.Assess(
            dto.FormatVersion is null ? null : GraphFormat.Parse(dto.FormatVersion));

        if (!assessment.Supported) throw new InvalidOperationException(assessment.Caveat);

        return new GraphReadResult(ToGraph(dto, assessment), assessment);
    }

    /// <summary>
    /// Load a graph, discarding the format report. For round-trips and callers
    /// with nowhere to show a caveat; an unsupported version still throws.
    /// </summary>
    public static CodeGraph FromJson(string json) => Read(json).Graph;

    private static CodeGraph ToGraph(GraphDto dto, GraphFormatAssessment assessment)
    {
        var graph = new CodeGraph();

        // Provenance the elements cannot carry. A newer format can add a property
        // to a kind we do know, which leaves nothing locally strange to spot at
        // save time -- so the origin is recorded from the stamp, and from any
        // origins the file was already carrying, rather than inferred later.
        if (assessment.Version is { } v && (v.Major, v.Minor).CompareTo((GraphFormat.Current.Major, GraphFormat.Current.Minor)) > 0)
            graph.ForeignFormatVersions.Add(v.ToString());
        foreach (var origin in dto.ForeignData?.FromVersions ?? [])
            graph.ForeignFormatVersions.Add(origin);

        foreach (var n in dto.Nodes)
        {
            var (type, foreign) = ParseType<NodeType>(n.Type);
            var node = new GraphNode
            {
                Id = n.Id,
                Type = type,
                ForeignType = foreign,
                Name = n.Name,
                FilePath = n.FilePath,
                LineStart = n.LineStart,
                LineEnd = n.LineEnd
            };
            foreach (var p in n.Properties) node.Properties[p.Key] = NormalizeValue(p.Value);
            foreach (var l in n.Labels) node.Labels.Add(l);
            graph.AddNode(node);
        }
        foreach (var e in dto.Edges)
        {
            var (type, foreign) = ParseType<EdgeType>(e.Type);
            var edge = new GraphEdge
            {
                FromId = e.From,
                ToId = e.To,
                Type = type,
                ForeignType = foreign
            };
            foreach (var p in e.Properties) edge.Properties[p.Key] = NormalizeValue(p.Value);
            graph.AddEdge(edge);
        }
        return graph;
    }

    /// <summary>
    /// Map a wire kind name onto this build's vocabulary. A name we do not have
    /// is preserved rather than rejected: strict parsing here is what would stop
    /// a third-party extractor's first file from loading at all, and what would
    /// stop this build from reading a newer minor version it is promised to be
    /// able to read.
    /// </summary>
    private static (TEnum Type, string? ForeignType) ParseType<TEnum>(string name) where TEnum : struct, Enum
    {
        // Case-insensitive because the wire form is camelCase and the enum is Pascal.
        if (Enum.TryParse<TEnum>(name, ignoreCase: true, out var parsed)) return (parsed, null);
        return (Enum.Parse<TEnum>("Unknown"), name);
    }

    /// <summary>
    /// Deserialized property values arrive as JsonElement; convert them back to the
    /// CLR types the graph was built with so typed GetProperty access keeps working.
    /// </summary>
    private static object NormalizeValue(object value)
    {
        if (value is not JsonElement je) return value;

        return je.ValueKind switch
        {
            JsonValueKind.String => je.GetString() ?? string.Empty,
            JsonValueKind.Number => je.TryGetInt32(out var i) ? i
                : je.TryGetInt64(out var l) ? l
                : (object)je.GetDouble(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Array => NormalizeArray(je),
            JsonValueKind.Object => NormalizeObject(je),
            _ => value
        };
    }

    /// <summary>
    /// Objects become dictionaries rather than surviving as JsonElement.
    /// </summary>
    /// <remarks>
    /// Without this a nested property value behaves differently either side of a save: typed on
    /// the graph that built it, a raw JsonElement on the graph that read it back. Nothing stored
    /// an object-valued property before attribute arguments did, so the gap was unexercised
    /// rather than harmless -- and it is the drift formatVersion exists to catch, slipping under
    /// it because the version is identical on both sides.
    /// </remarks>
    private static Dictionary<string, object> NormalizeObject(JsonElement je)
    {
        var map = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var property in je.EnumerateObject())
            map[property.Name] = NormalizeValue(property.Value);
        return map;
    }

    private static object NormalizeArray(JsonElement je)
    {
        var items = je.EnumerateArray().Select(e => NormalizeValue(e)).ToList();
        // Graph builders store homogeneous string lists (viewDataKeys, methods, ...);
        // restore that shape so GetProperty<List<string>> works after a round-trip.
        return items.All(i => i is string) ? items.Cast<string>().ToList() : items;
    }

    /// <summary>
    /// Export a subgraph (set of nodes + their interconnecting edges) as a compact
    /// research.json-compatible document for LLM consumption.
    /// </summary>
    public static string ToResearchDocument(CodeGraph graph, IEnumerable<string> nodeIds, string query, double relevanceThreshold = 0.0) =>
        ToResearchDocument(graph, nodeIds.ToDictionary(id => id, _ => 1.0), query, relevanceThreshold);

    /// <summary>
    /// Export a relevance-scored subgraph. Nodes below the threshold are dropped,
    /// along with any edge touching a dropped node; file:line anchors and the score
    /// are emitted so LLM consumers can rank and open what they read about.
    /// </summary>
    public static string ToResearchDocument(CodeGraph graph, IReadOnlyDictionary<string, double> nodeRelevance, string query, double relevanceThreshold = 0.0)
    {
        var kept = nodeRelevance
            .Where(kv => kv.Value >= relevanceThreshold)
            .ToDictionary(kv => kv.Key, kv => kv.Value);

        var subNodes = graph.Nodes.Where(n => kept.ContainsKey(n.Id)).ToList();
        var subEdges = graph.Edges.Where(e => kept.ContainsKey(e.FromId) && kept.ContainsKey(e.ToId)).ToList();

        var doc = new ResearchDocumentDto
        {
            Query = query,
            GeneratedAt = DateTimeOffset.UtcNow,
            Nodes = subNodes.Select(n =>
            {
                var dto = ToNodeDto(n);
                dto.Relevance = kept[n.Id];
                return dto;
            }).OrderByDescending(n => n.Relevance).ToList(),
            Edges = subEdges.Select(e => new EdgeDto
            {
                From = e.FromId,
                To = e.ToId,
                Type = TypeName(e.Type, e.ForeignType),
                Properties = e.Properties
            }).ToList()
        };

        return JsonSerializer.Serialize(doc, Options);
    }

    private sealed class GraphDto
    {
        // First so it lands at the top of the file: a reader deciding whether it
        // can read this document should not have to scan past 20k nodes to find out.
        public string? FormatVersion { get; set; }

        // Second for the same reason, and omitted entirely when the graph holds
        // nothing foreign -- its presence is the signal.
        public ForeignDataDto? ForeignData { get; set; }

        public List<NodeDto> Nodes { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
    }

    /// <summary>
    /// The in-file caveat: what this graph carries that its writer did not model.
    /// A tool response warns the session that ran the load; this warns everything
    /// downstream of the file, which is where the graph actually gets reused.
    /// </summary>
    private sealed class ForeignDataDto
    {
        [JsonPropertyName(".comment")]
        public IReadOnlyList<string> Comment { get; set; } = [];

        /// <summary>Newer format versions this data was read from, if any.</summary>
        public List<string>? FromVersions { get; set; }

        public List<string>? NodeTypes { get; set; }
        public List<string>? EdgeTypes { get; set; }
        public int Nodes { get; set; }
        public int Edges { get; set; }
    }

    private sealed class NodeDto
    {
        public string Id { get; set; } = string.Empty;
        // A string, not the enum: System.Text.Json throws on an enum name it does
        // not know, which would make an unrecognised kind fatal to the whole load.
        public string Type { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? FilePath { get; set; }
        public int? LineStart { get; set; }
        public int? LineEnd { get; set; }
        public double? Relevance { get; set; }
        public Dictionary<string, object> Properties { get; set; } = new();
        public List<string> Labels { get; set; } = new();
    }

    private sealed class EdgeDto
    {
        public string From { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public Dictionary<string, object> Properties { get; set; } = new();
    }

    private sealed class ResearchDocumentDto
    {
        public string Query { get; set; } = string.Empty;
        public DateTimeOffset GeneratedAt { get; set; }
        public List<NodeDto> Nodes { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
    }
}
