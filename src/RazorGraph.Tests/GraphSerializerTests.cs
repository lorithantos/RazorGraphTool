namespace RazorGraph.Tests;

using System.Text.Json;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using Xunit;

public class GraphSerializerTests
{
    private static CodeGraph BuildGraph()
    {
        var graph = new CodeGraph();

        var page = new GraphNode
        {
            Id = "page:Pages/Index.cshtml",
            Type = NodeType.RazorPage,
            Name = "Index",
            FilePath = @"C:\app\Pages\Index.cshtml",
            LineStart = 1,
            LineEnd = 20
        };
        page.SetProperty("modelType", "IndexModel");
        page.SetProperty("viewDataKeys", new List<string> { "Title", "Description" });
        graph.AddNode(page);

        graph.AddNode(new GraphNode { Id = "pm:App.IndexModel", Type = NodeType.PageModel, Name = "IndexModel" });
        graph.AddNode(new GraphNode { Id = "page:_Card", Type = NodeType.PartialView, Name = "_Card" });

        graph.AddEdge(new GraphEdge { FromId = "page:Pages/Index.cshtml", ToId = "pm:App.IndexModel", Type = EdgeType.PageServedBy });

        var partialEdge = new GraphEdge { FromId = "page:Pages/Index.cshtml", ToId = "page:_Card", Type = EdgeType.RendersPartial };
        partialEdge.Properties["line"] = 8;
        partialEdge.Properties["isTagHelper"] = true;
        graph.AddEdge(partialEdge);

        return graph;
    }

    [Fact]
    public void RoundTrip_PreservesCountsAndScalars()
    {
        var original = BuildGraph();

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(original));

        Assert.Equal(original.Nodes.Count, restored.Nodes.Count);
        Assert.Equal(original.Edges.Count, restored.Edges.Count);

        var page = restored.GetNode("page:Pages/Index.cshtml")!;
        Assert.Equal(NodeType.RazorPage, page.Type);
        Assert.Equal(1, page.LineStart);
        Assert.Equal(20, page.LineEnd);
    }

    [Fact]
    public void RoundTrip_PreservesTypedPropertyAccess()
    {
        // Regression: deserialized properties were left as JsonElement, making
        // every typed GetProperty call return default on a loaded graph.
        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(BuildGraph()));

        var page = restored.GetNode("page:Pages/Index.cshtml")!;
        Assert.Equal("IndexModel", page.GetProperty<string>("modelType"));
        Assert.Equal(new List<string> { "Title", "Description" }, page.GetProperty<List<string>>("viewDataKeys"));

        var edge = restored.Outgoing("page:Pages/Index.cshtml").First(e => e.Type == EdgeType.RendersPartial);
        Assert.Equal(8, edge.GetProperty<int>("line"));
        Assert.True(edge.GetProperty<bool>("isTagHelper"));
    }

    // The escape data is exactly the shape the serializer round-trips
    // generically (string lists); this pins that no bespoke handling is needed.
    [Fact]
    public void RoundTrip_PreservesThrowsAndGuardLists()
    {
        var graph = new CodeGraph();
        var thrower = new GraphNode { Id = "m:App.T.Boom()", Type = NodeType.Method, Name = "Boom" };
        thrower.SetProperty("throws", new List<string> { "System.InvalidOperationException" });
        thrower.SetProperty("entryPointKind", "asyncVoid");
        graph.AddNode(thrower);
        graph.AddNode(new GraphNode { Id = "m:App.T.Caller()", Type = NodeType.Method, Name = "Caller" });

        var call = new GraphEdge { FromId = "m:App.T.Caller()", ToId = "m:App.T.Boom()", Type = EdgeType.Calls };
        call.Properties["guardedBy"] = new List<string> { "*" };
        graph.AddEdge(call);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));

        var node = restored.GetNode("m:App.T.Boom()")!;
        Assert.Equal(
            new List<string> { "System.InvalidOperationException" },
            node.GetProperty<List<string>>("throws"));
        Assert.Equal("asyncVoid", node.GetProperty<string>("entryPointKind"));
        Assert.Equal(
            new List<string> { "*" },
            restored.Outgoing("m:App.T.Caller()").Single().GetProperty<List<string>>("guardedBy"));
    }

    [Fact]
    public void RoundTrip_RestoredGraphHasWorkingAdjacency()
    {
        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(2, restored.Outgoing("page:Pages/Index.cshtml").Count);
        Assert.Single(restored.Incoming("pm:App.IndexModel"));
    }

    // ---- Format version ----------------------------------------------------
    // The stamp exists for readers that did not write the file. Every assertion
    // below is about a mismatch being SAID, not merely survived.

    private static string GraphJson(string? formatVersion) =>
        formatVersion is null
            ? """{"nodes":[],"edges":[]}"""
            : $$"""{"formatVersion":"{{formatVersion}}","nodes":[],"edges":[]}""";

    [Fact]
    public void ToJson_StampsCurrentFormatVersion()
    {
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(GraphFormat.Current.ToString(), doc.RootElement.GetProperty("formatVersion").GetString());
    }

    [Fact]
    public void Read_CurrentVersion_HasNoCaveat()
    {
        var result = GraphSerializer.Read(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(GraphFormat.Current, result.Format.Version);
        Assert.Null(result.Format.Caveat);
    }

    [Fact]
    public void Read_UnstampedGraph_LoadsAndSaysSo()
    {
        // Every graph saved before this change is unstamped. Refusing them would
        // be a regression; loading them silently is the drift the stamp prevents.
        var result = GraphSerializer.Read(GraphJson(null));

        Assert.Null(result.Format.Version);
        Assert.Equal("unstamped", result.Format.Display);
        Assert.Contains("no formatVersion", result.Format.Caveat);
    }

    [Fact]
    public void Read_NewerMajor_Refuses()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => GraphSerializer.Read(GraphJson($"{GraphFormat.Current.Major + 1}.0")));

        Assert.Contains("newer than this build reads", ex.Message);
    }

    [Fact]
    public void Read_NewerMinor_LoadsWithCaveat()
    {
        // Minor is additive by contract: unknown vocabulary must not be fatal,
        // or a third-party extractor can never ship a new node kind.
        var result = GraphSerializer.Read(GraphJson($"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}"));

        Assert.Contains("newer minor", result.Format.Caveat);
    }

    [Fact]
    public void Read_OlderMajor_LoadsWithCaveat()
    {
        var result = GraphSerializer.Read(GraphJson("0.9"));

        Assert.Equal(new GraphFormatVersion(0, 9), result.Format.Version);
        Assert.Contains("predates", result.Format.Caveat);
    }

    [Fact]
    public void Read_UnparseableVersion_Refuses()
    {
        // Disagreeing about the format of the format is worse evidence of drift
        // than no stamp, so this is louder than the unstamped case, not quieter.
        var ex = Assert.Throws<InvalidOperationException>(() => GraphSerializer.Read(GraphJson("v1")));

        Assert.Contains("Unreadable formatVersion", ex.Message);
    }

    [Fact]
    public void Read_PreservesGraphContentAlongsideVersion()
    {
        var result = GraphSerializer.Read(GraphSerializer.ToJson(BuildGraph()));

        Assert.Equal(3, result.Graph.Nodes.Count);
        Assert.Equal(2, result.Graph.Edges.Count);
    }

    [Fact]
    public void ResearchDocument_FiltersByRelevanceThreshold()
    {
        var graph = BuildGraph();
        var relevance = new Dictionary<string, double>
        {
            ["page:Pages/Index.cshtml"] = 1.0,
            ["pm:App.IndexModel"] = 0.5,
            ["page:_Card"] = 0.2
        };

        var json = GraphSerializer.ToResearchDocument(graph, relevance, "how is Index served?", relevanceThreshold: 0.5);
        using var doc = JsonDocument.Parse(json);

        var nodeIds = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("id").GetString())
            .ToList();

        Assert.Contains("page:Pages/Index.cshtml", nodeIds);
        Assert.Contains("pm:App.IndexModel", nodeIds);
        Assert.DoesNotContain("page:_Card", nodeIds);

        // Edges touching a dropped node are dropped with it.
        var edges = doc.RootElement.GetProperty("edges").EnumerateArray().ToList();
        Assert.Single(edges);
        Assert.Equal("pm:App.IndexModel", edges[0].GetProperty("to").GetString());

        Assert.Equal("how is Index served?", doc.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public void ResearchDocument_EmitsRelevanceAndLineAnchors()
    {
        var graph = BuildGraph();
        var relevance = new Dictionary<string, double> { ["page:Pages/Index.cshtml"] = 0.75 };

        var json = GraphSerializer.ToResearchDocument(graph, relevance, "q");
        using var doc = JsonDocument.Parse(json);

        var node = doc.RootElement.GetProperty("nodes").EnumerateArray().Single();
        Assert.Equal(0.75, node.GetProperty("relevance").GetDouble());
        Assert.Equal(1, node.GetProperty("lineStart").GetInt32());
    }

    [Fact]
    public void ResearchDocument_IdOverloadIncludesAllGivenIds()
    {
        var graph = BuildGraph();

        var json = GraphSerializer.ToResearchDocument(graph, new[] { "page:Pages/Index.cshtml", "pm:App.IndexModel" }, "q");
        using var doc = JsonDocument.Parse(json);

        Assert.Equal(2, doc.RootElement.GetProperty("nodes").GetArrayLength());
    }
}
