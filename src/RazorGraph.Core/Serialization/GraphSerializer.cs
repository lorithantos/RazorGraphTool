namespace RazorGraph.Core.Serialization;

using System.Text.Json;
using System.Text.Json.Serialization;
using RazorGraph.Core.Graph;

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
            Nodes = graph.Nodes.Select(n => new NodeDto
            {
                Id = n.Id,
                Type = n.Type,
                Name = n.Name,
                FilePath = n.FilePath,
                LineStart = n.LineStart,
                LineEnd = n.LineEnd,
                Properties = n.Properties,
                Labels = n.Labels
            }).ToList(),
            Edges = graph.Edges.Select(e => new EdgeDto
            {
                From = e.FromId,
                To = e.ToId,
                Type = e.Type,
                Properties = e.Properties
            }).ToList()
        };
        return JsonSerializer.Serialize(dto, Options);
    }

    public static CodeGraph FromJson(string json)
    {
        var dto = JsonSerializer.Deserialize<GraphDto>(json, Options)
            ?? throw new InvalidOperationException("Failed to deserialize graph JSON.");

        var graph = new CodeGraph();
        foreach (var n in dto.Nodes)
        {
            var node = new GraphNode
            {
                Id = n.Id,
                Type = n.Type,
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
            var edge = new GraphEdge
            {
                FromId = e.From,
                ToId = e.To,
                Type = e.Type
            };
            foreach (var p in e.Properties) edge.Properties[p.Key] = NormalizeValue(p.Value);
            graph.AddEdge(edge);
        }
        return graph;
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
            _ => value
        };
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
            Nodes = subNodes.Select(n => new NodeDto
            {
                Id = n.Id,
                Type = n.Type,
                Name = n.Name,
                FilePath = n.FilePath,
                LineStart = n.LineStart,
                LineEnd = n.LineEnd,
                Relevance = kept[n.Id],
                Properties = n.Properties,
                Labels = n.Labels
            }).OrderByDescending(n => n.Relevance).ToList(),
            Edges = subEdges.Select(e => new EdgeDto
            {
                From = e.FromId,
                To = e.ToId,
                Type = e.Type,
                Properties = e.Properties
            }).ToList()
        };

        return JsonSerializer.Serialize(doc, Options);
    }

    private sealed class GraphDto
    {
        public List<NodeDto> Nodes { get; set; } = new();
        public List<EdgeDto> Edges { get; set; } = new();
    }

    private sealed class NodeDto
    {
        public string Id { get; set; } = string.Empty;
        public NodeType Type { get; set; }
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
        public EdgeType Type { get; set; }
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
