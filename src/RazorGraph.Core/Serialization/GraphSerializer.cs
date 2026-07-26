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
            foreach (var p in n.Properties) node.Properties[p.Key] = p.Value;
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
            foreach (var p in e.Properties) edge.Properties[p.Key] = p.Value;
            graph.AddEdge(edge);
        }
        return graph;
    }

    /// <summary>
    /// Export a subgraph (set of nodes + their interconnecting edges) as a compact
    /// research.json-compatible document for LLM consumption.
    /// </summary>
    public static string ToResearchDocument(CodeGraph graph, IEnumerable<string> nodeIds, string query, double relevanceThreshold = 0.0)
    {
        var idSet = new HashSet<string>(nodeIds);
        var subNodes = graph.Nodes.Where(n => idSet.Contains(n.Id)).ToList();
        var subEdges = graph.Edges.Where(e => idSet.Contains(e.FromId) && idSet.Contains(e.ToId)).ToList();

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
                Properties = n.Properties,
                Labels = n.Labels
            }).ToList(),
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
