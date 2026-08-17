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

    /// <summary>
    /// The same regression one level down: objects were the one JSON kind
    /// NormalizeValue did not rebuild, so a nested value was a Dictionary on the
    /// graph that built it and a raw JsonElement on the graph that read it back,
    /// from identical bytes and under an identical formatVersion.
    /// </summary>
    [Fact]
    public void RoundTrip_PreservesTypedAccessThroughNestedObjects()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "m:A()", Type = NodeType.Method, Name = "A" });
        graph.AddNode(new GraphNode { Id = "ext:X", Type = NodeType.ExternalType, Name = "X" });

        var edge = new GraphEdge { FromId = "m:A()", ToId = "ext:X", Type = EdgeType.DecoratedBy };
        edge.Properties["named"] = new Dictionary<string, object>
        {
            ["Skip"] = "flaky on CI",
            ["Timeout"] = 500
        };
        graph.AddEdge(edge);

        var restored = GraphSerializer.FromJson(GraphSerializer.ToJson(graph));
        var named = restored.Outgoing("m:A()").Single().GetProperty<Dictionary<string, object>>("named");

        Assert.NotNull(named);
        Assert.Equal("flaky on CI", named!["Skip"]);
        Assert.Equal(500, named["Timeout"]);
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

    // ---- Foreign vocabulary ------------------------------------------------
    // A kind this build has no enum member for must load, survive a save
    // unchanged, and be declared in the file as untrustworthy. All three:
    // throwing blocks third-party extractors, rewriting corrupts their graph,
    // and staying quiet is the drift the format stamp exists to catch.

    private static string ForeignGraphJson(string? formatVersion = null) =>
        $$"""
        {
          {{(formatVersion is null ? "" : $"\"formatVersion\": \"{formatVersion}\",")}}
          "nodes": [
            { "id": "mod:app.init", "type": "module", "name": "init" },
            { "id": "m:App.Go()", "type": "method", "name": "Go" }
          ],
          "edges": [
            { "from": "mod:app.init", "to": "m:App.Go()", "type": "requires" }
          ]
        }
        """;

    [Fact]
    public void Read_UnknownNodeAndEdgeKinds_LoadInsteadOfThrowing()
    {
        // The blocker this whole change exists to remove: a Lua extractor's first
        // file used to take load_graph down entirely.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        var module = graph.GetNode("mod:app.init")!;
        Assert.Equal(NodeType.Unknown, module.Type);
        Assert.Equal("module", module.ForeignType);
        Assert.Equal(EdgeType.Unknown, graph.Edges.Single().Type);
        Assert.Equal("requires", graph.Edges.Single().ForeignType);
    }

    [Fact]
    public void Read_KnownKindsAlongsideForeignOnes_StillResolve()
    {
        // Tolerance must be per-kind, not a whole-document fallback.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        var method = graph.GetNode("m:App.Go()")!;
        Assert.Equal(NodeType.Method, method.Type);
        Assert.Null(method.ForeignType);
    }

    [Fact]
    public void Read_GenuineUnknownKind_IsNotMarkedForeign()
    {
        // "unknown" is a real member of the vocabulary. Only a name we cannot map
        // is foreign, or every unclassified node would claim foreign provenance.
        var graph = GraphSerializer.Read(
            """{"nodes":[{"id":"x","type":"unknown","name":"x"}],"edges":[]}""").Graph;

        Assert.Equal(NodeType.Unknown, graph.GetNode("x")!.Type);
        Assert.Null(graph.GetNode("x")!.ForeignType);
    }

    [Fact]
    public void DisplayType_ShowsForeignNameRatherThanUnknown()
    {
        // Preserving a kind in storage and rendering it as "Unknown" would discard
        // it at the one moment it matters. Every report and tool response reads
        // this property.
        var graph = GraphSerializer.Read(ForeignGraphJson()).Graph;

        Assert.Equal("module", graph.GetNode("mod:app.init")!.DisplayType);
        Assert.Equal("Method", graph.GetNode("m:App.Go()")!.DisplayType);
        Assert.Equal("requires", graph.Edges.Single().DisplayType);
    }

    [Fact]
    public void RoundTrip_ForeignKindNamesSurviveVerbatim()
    {
        var once = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson()).Graph);
        using var doc = JsonDocument.Parse(once);

        var types = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Contains("module", types);
        Assert.Equal("requires", doc.RootElement.GetProperty("edges").EnumerateArray()
            .Single().GetProperty("type").GetString());
    }

    [Fact]
    public void ToJson_KnownKinds_KeepTheirExistingWireNames()
    {
        // Regression guard on the string-typed DTO: the previously published
        // server strict-parses these names, so a casing change would lock it out
        // of every graph written from here on.
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        var types = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString()).ToList();
        Assert.Contains("razorPage", types);
        Assert.Contains("pageModel", types);
        Assert.Contains("pageServedBy", doc.RootElement.GetProperty("edges").EnumerateArray()
            .Select(e => e.GetProperty("type").GetString()).ToList());
    }

    [Fact]
    public void ToJson_ForeignData_DeclaredInTheFileWithItsCaveat()
    {
        // The caveat goes in the FILE, not only in the load response: the file
        // outlives the session that wrote it and is what gets reused.
        var json = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson()).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));

        Assert.Contains("not uniformly trustworthy", comment);
        Assert.Contains($"Only data belonging to format {GraphFormat.Current}", comment);
        Assert.Equal("module", foreign.GetProperty("nodeTypes").EnumerateArray().Single().GetString());
        Assert.Equal("requires", foreign.GetProperty("edgeTypes").EnumerateArray().Single().GetString());
        Assert.Equal(1, foreign.GetProperty("nodes").GetInt32());
        Assert.Equal(1, foreign.GetProperty("edges").GetInt32());
    }

    [Fact]
    public void ToJson_CleanGraph_HasNoForeignDataBlock()
    {
        // Presence is the signal, so an empty block would cry wolf on every graph.
        using var doc = JsonDocument.Parse(GraphSerializer.ToJson(BuildGraph()));

        Assert.False(doc.RootElement.TryGetProperty("foreignData", out _));
    }

    [Fact]
    public void ToJson_NewerMinorOrigin_IsNamedInTheFile()
    {
        var newerMinor = $"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}";

        var json = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson(newerMinor)).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.Equal(newerMinor, foreign.GetProperty("fromVersions").EnumerateArray().Single().GetString());

        // The writer stamps ITS OWN version -- claiming the newer one would assert
        // semantics this build never applied.
        Assert.Equal(GraphFormat.Current.ToString(), doc.RootElement.GetProperty("formatVersion").GetString());

        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains($"read from format {newerMinor}", comment);
    }

    [Fact]
    public void RoundTrip_ForeignProvenanceSurvivesRepeatedSaves()
    {
        // Provenance has to survive the chain, not just the first hop: a graph
        // that loses its origin on the second save has laundered itself clean.
        var newerMinor = $"{GraphFormat.Current.Major}.{GraphFormat.Current.Minor + 1}";

        var first = GraphSerializer.ToJson(GraphSerializer.Read(ForeignGraphJson(newerMinor)).Graph);
        var second = GraphSerializer.ToJson(GraphSerializer.Read(first).Graph);

        using var doc = JsonDocument.Parse(second);
        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.Equal(newerMinor, foreign.GetProperty("fromVersions").EnumerateArray().Single().GetString());
        Assert.Equal("module", foreign.GetProperty("nodeTypes").EnumerateArray().Single().GetString());
    }

    [Fact]
    public void ToJson_ForeignKindsWithoutANewerVersion_SayTheyCameFromAnExtension()
    {
        // Same hazard, different origin: an extractor extending this version.
        // Claiming a newer version produced it would be a fabricated provenance.
        var json = GraphSerializer.ToJson(
            GraphSerializer.Read(ForeignGraphJson(GraphFormat.Current.ToString())).Graph);
        using var doc = JsonDocument.Parse(json);

        var foreign = doc.RootElement.GetProperty("foreignData");
        Assert.False(foreign.TryGetProperty("fromVersions", out _));

        var comment = string.Join(" ", foreign.GetProperty(".comment").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("extractor extending the format", comment);
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
