namespace RazorGraph.Tests;

using System.Text.Json;
using ModelContextProtocol;
using RazorGraph.Core.Graph;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// First tests at the MCP tool layer: ExceptionEscapeTools over a hand-built
/// graph in a plain GraphStore, results parsed back out of the JSON the model
/// would see. Covers exception_escapes; the rest of the tool surface (the
/// other tool-family classes) is a known gap.
/// </summary>
public class GraphToolsTests
{
    [Fact]
    public void TheAdvertisedNodeTypes_AreExactlyTheRealOnes()
    {
        // The list in the tool description and the refusal message is a const,
        // because an attribute argument has to be — so it can drift from the enum,
        // and it did: NamedBinding shipped unlisted, leaving a caller no way to
        // learn the type existed. Asserted through the public surface, which is
        // the text a caller actually gets.
        var store = new GraphStore();
        store.Add(new CodeGraph(), source: "test://hand-built", requestedId: "g");
        var tools = new GraphNavigationTools(store);

        var message = Assert.Throws<McpException>(() => tools.FindNodes("NotANodeType")).Message;

        var start = message.IndexOf("Valid types: ", StringComparison.Ordinal) + "Valid types: ".Length;
        var end = message.IndexOf(". This graph", StringComparison.Ordinal);
        var advertised = message[start..end].Split(", ").ToHashSet(StringComparer.Ordinal);

        // Unknown is the parser's failure value, not something to ask for.
        var real = Enum.GetNames<NodeType>().Where(n => n != nameof(NodeType.Unknown)).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(real.Except(advertised));
        Assert.Empty(advertised.Except(real));
    }

    [Fact]
    public void FindNodes_WithoutNodeType_SearchesEveryKindByName()
    {
        // nodeType used to be a required argument, so the call to make when you
        // know a name and not its kind failed in the SDK before the tool ran,
        // as "An error occurred invoking 'find_nodes'". Found 2026-09-04 by
        // hitting it three times in one session and guessing a kind each time.
        var tools = new GraphNavigationTools(StoreOver(MixedKindGraph()));

        using var doc = JsonDocument.Parse(tools.FindNodes(nameContains: "Widget"));
        var kinds = doc.RootElement.GetProperty("nodes").EnumerateArray()
            .Select(n => n.GetProperty("type").GetString())
            .OrderBy(k => k, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(["Class", "Method", "Property"], kinds);
        Assert.Equal(3, doc.RootElement.GetProperty("totalMatches").GetInt32());
    }

    [Fact]
    public void FindNodes_WithNeitherTypeNorName_RefusesNamingBoth()
    {
        // The refusal has to say what to supply: the SDK's message named nothing.
        var tools = new GraphNavigationTools(StoreOver(MixedKindGraph()));

        var message = Assert.Throws<McpException>(() => tools.FindNodes()).Message;

        Assert.Contains("nodeType", message, StringComparison.Ordinal);
        Assert.Contains("nameContains", message, StringComparison.Ordinal);
    }

    // ---- get_node: narrow by default -----------------------------------------

    // The shape that provoked this: one Calls edge worth reading, buried under a
    // hundred-odd Covers edges nobody asked for. The full answer to a question
    // about the Calls edge used to cost every Covers row.

    [Fact]
    public void GetNode_ByDefault_ListsOutgoingAndOnlySummarisesIncoming()
    {
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId));
        var outgoing = doc.RootElement.GetProperty("outgoing");
        var incoming = doc.RootElement.GetProperty("incoming");

        Assert.True(outgoing.GetProperty("expanded").GetBoolean());
        Assert.Equal(1, outgoing.GetProperty("edges").GetArrayLength());

        // Summarised: the shape is visible, the rows are not.
        Assert.False(incoming.GetProperty("expanded").GetBoolean());
        Assert.False(incoming.TryGetProperty("edges", out _));

        // Nothing is hidden — the totals are still reported.
        Assert.Equal(119, incoming.GetProperty("total").GetInt32());
        var byType = incoming.GetProperty("byType").EnumerateArray()
            .ToDictionary(e => e.GetProperty("type").GetString()!, e => e.GetProperty("count").GetInt32());
        Assert.Equal(118, byType["Covers"]);
        Assert.Equal(1, byType["Calls"]);
    }

    [Fact]
    public void GetNode_Default_CostsAFractionOfTheFullAnswer()
    {
        // The point of the change, asserted as a ratio rather than a byte count
        // so it cannot go flaky on formatting.
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        var narrow = tools.GetNode(CoveredMethodId).Length;
        var wide = tools.GetNode(CoveredMethodId, edges: "all", limit: 1000).Length;

        Assert.True(narrow * 10 < wide, $"narrow {narrow} vs wide {wide}: expected an order of magnitude");
    }

    [Fact]
    public void GetNode_EdgesIncoming_ReturnsTheRows()
    {
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId, edges: "incoming", limit: 1000));
        var incoming = doc.RootElement.GetProperty("incoming");

        Assert.True(incoming.GetProperty("expanded").GetBoolean());
        Assert.Equal(119, incoming.GetProperty("edges").GetArrayLength());
        Assert.False(incoming.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void GetNode_EdgeType_NarrowsToTheOneEdgeThatWasWanted()
    {
        // The whole point: reach the single Calls edge without paying for 118
        // Covers rows to find it.
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(
            tools.GetNode(CoveredMethodId, edges: "incoming", edgeType: "Calls"));
        var incoming = doc.RootElement.GetProperty("incoming");

        var edge = Assert.Single(incoming.GetProperty("edges").EnumerateArray());
        Assert.Equal("Calls", edge.GetProperty("type").GetString());
        Assert.Equal("m:Web.Caller.Invoke()", edge.GetProperty("from").GetString());
    }

    [Fact]
    public void GetNode_EdgeTypeFilter_DoesNotChangeWhatTheOtherSideReports()
    {
        // Written after the first real call caught it: the filter was applied
        // before the summary, so asking for one incoming type reported the
        // OUTGOING side as total 0 on a node that has an outgoing edge. A
        // narrowing argument must not change what the unasked-for half claims
        // about itself — that is a confident wrong answer, not a smaller one.
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(
            tools.GetNode(CoveredMethodId, edges: "incoming", edgeType: "Covers"));

        var outgoing = doc.RootElement.GetProperty("outgoing");
        Assert.Equal(1, outgoing.GetProperty("total").GetInt32());
        Assert.Equal("Calls", outgoing.GetProperty("byType")[0].GetProperty("type").GetString());

        // And the expanded side reports the whole side's total beside the
        // filtered count, so "118 of 119" is legible rather than implied.
        var incoming = doc.RootElement.GetProperty("incoming");
        Assert.Equal(119, incoming.GetProperty("total").GetInt32());
        Assert.Equal(118, incoming.GetProperty("matching").GetInt32());
        Assert.Equal("Covers", incoming.GetProperty("filteredTo").GetString());
    }

    [Fact]
    public void GetNode_Limit_ReportsThatItBit()
    {
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId, edges: "incoming", limit: 10));
        var incoming = doc.RootElement.GetProperty("incoming");

        Assert.Equal(10, incoming.GetProperty("returned").GetInt32());
        Assert.Equal(119, incoming.GetProperty("total").GetInt32());
        Assert.True(incoming.GetProperty("truncated").GetBoolean());
    }

    [Fact]
    public void GetNode_AvailableFields_NamesTheExpansionsThisNodeCanTake()
    {
        // The menu is derived from the node, not a static list: a caller can
        // only ask for an edge type that is actually there.
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId));
        var fields = doc.RootElement.GetProperty("availableFields");

        var edges = fields.GetProperty("edges").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["none", "outgoing", "incoming", "all"], edges);

        var types = fields.GetProperty("edgeType").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["Calls", "Covers"], types);
    }

    [Fact]
    public void GetNode_UnknownEdgeType_IsRefusedNamingWhatTheNodeHas()
    {
        // Never an empty expansion: "no Implements edges" and "you typed it
        // wrong" would read identically, and the first is a real conclusion.
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        var message = Assert.Throws<McpException>(
            () => tools.GetNode(CoveredMethodId, edges: "incoming", edgeType: "Implements")).Message;

        Assert.Contains("Implements", message, StringComparison.Ordinal);
        Assert.Contains("Calls", message, StringComparison.Ordinal);
        Assert.Contains("Covers", message, StringComparison.Ordinal);
    }

    [Fact]
    public void GetNode_UnknownEdgesValue_IsRefusedNamingTheValidOnes()
    {
        var tools = new GraphNavigationTools(StoreOver(WellCoveredGraph()));

        var message = Assert.Throws<McpException>(
            () => tools.GetNode(CoveredMethodId, edges: "both")).Message;

        Assert.Contains("none, outgoing, incoming, all", message, StringComparison.Ordinal);
    }

    // ---- Coverage paging --------------------------------------------------------

    // A well-covered method has hundreds of tests. Reading 130 of them to answer
    // a yes/no question is what provoked this; the count and the depth histogram
    // are usually the whole answer.

    [Fact]
    public void CoveringTests_PagesAndReportsHowToContinue()
    {
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        using var first = JsonDocument.Parse(tools.CoveringTests(CoveredMethodId, limit: 10));
        Assert.Equal(10, first.RootElement.GetProperty("returned").GetInt32());
        Assert.Equal(60, first.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.True(first.RootElement.GetProperty("truncated").GetBoolean());
        Assert.Equal(10, first.RootElement.GetProperty("nextOffset").GetInt32());
    }

    [Fact]
    public void CoveringTests_PagingVisitsEveryRowExactlyOnce()
    {
        // The property that makes offset paging correct, and the reason the
        // query had to be given a TOTAL order: depth alone left ties to edge
        // enumeration, and an offset into a partial order repeats and skips.
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        var seen = new List<string>();
        int? offset = 0;
        while (offset is { } at)
        {
            using var page = JsonDocument.Parse(tools.CoveringTests(CoveredMethodId, limit: 7, offset: at));
            seen.AddRange(page.RootElement.GetProperty("tests").EnumerateArray()
                .Select(t => t.GetProperty("node").GetProperty("id").GetString()!));
            offset = page.RootElement.TryGetProperty("nextOffset", out var next) ? next.GetInt32() : null;
        }

        Assert.Equal(60, seen.Count);
        Assert.Equal(60, seen.Distinct().Count());
    }

    [Fact]
    public void CoveringTests_LastPage_HasNoNextOffset()
    {
        // Absence is the "you are done" signal, so a caller never compares two
        // numbers to find out whether it finished.
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        using var doc = JsonDocument.Parse(tools.CoveringTests(CoveredMethodId, limit: 10, offset: 50));
        Assert.Equal(10, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
        Assert.False(doc.RootElement.TryGetProperty("nextOffset", out _));
    }

    [Fact]
    public void CoveringTests_DepthHistogram_DescribesTheWholeSet_NotThePage()
    {
        // The histogram is what a caller reads to decide whether to page at
        // all, so a page or a filter must not shrink it.
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        using var doc = JsonDocument.Parse(tools.CoveringTests(CoveredMethodId, limit: 3, depth: 1));
        var counts = doc.RootElement.GetProperty("depthCounts").EnumerateArray()
            .ToDictionary(d => d.GetProperty("depth").GetInt32(), d => d.GetProperty("count").GetInt32());

        Assert.Equal(20, counts[1]);
        Assert.Equal(20, counts[2]);
        Assert.Equal(20, counts[3]);

        // …while the filtered total is the filtered one.
        Assert.Equal(20, doc.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("filteredToDepth").GetInt32());
    }

    [Fact]
    public void CoveringTests_UnknownDepth_IsRefusedNamingTheDepthsPresent()
    {
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        var message = Assert.Throws<McpException>(
            () => tools.CoveringTests(CoveredMethodId, depth: 9)).Message;

        Assert.Contains("depth 9", message, StringComparison.Ordinal);
        Assert.Contains("1, 2, 3", message, StringComparison.Ordinal);
    }

    [Fact]
    public void UncoveredMethods_PagesOverAStableOrder()
    {
        var tools = new CoverageTools(StoreOver(WideCoverageGraph()));

        using var a = JsonDocument.Parse(tools.UncoveredMethods(limit: 5));
        using var b = JsonDocument.Parse(tools.UncoveredMethods(limit: 5));

        var first = a.RootElement.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();
        var again = b.RootElement.GetProperty("methods").EnumerateArray().Select(m => m.GetProperty("id").GetString()).ToList();

        Assert.Equal(first, again);
        Assert.Equal(first.OrderBy(i => i, StringComparer.Ordinal), first);
    }

    /// <summary>
    /// One method reached by 60 tests spread over three depths, plus a handful
    /// of production methods nothing reaches.
    /// </summary>
    private static CodeGraph WideCoverageGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        graph.AddNode(new GraphNode { Id = CoveredMethodId, Type = NodeType.Method, Name = "Advance" });

        for (var i = 0; i < 60; i++)
        {
            var testId = $"m:Web.Tests.DoctrineTests.Case{i:D2}()";
            var test = new GraphNode { Id = testId, Type = NodeType.Method, Name = $"Case{i:D2}" };
            test.SetProperty("isTest", true);
            graph.AddNode(test);

            var edge = new GraphEdge { FromId = testId, ToId = CoveredMethodId, Type = EdgeType.Covers };
            edge.Properties["depth"] = (i % 3) + 1;
            graph.AddEdge(edge);
        }

        // Unreached production methods, deliberately added out of id order so
        // the ordering assertion means something.
        foreach (var name in new[] { "Zeta", "Alpha", "Mu" })
            graph.AddNode(new GraphNode { Id = $"m:Web.Idle.{name}()", Type = NodeType.Method, Name = name });

        return graph;
    }

    // ---- The node's own payload is narrow too -----------------------------------

    // Measured on a 2,098-node graph: scalars are 245KB total, about 117 bytes a
    // node, and are what orients a reader. Lists are 37KB on 229 nodes and own
    // every fat payload. So scalars inline, lists counted.

    [Fact]
    public void GetNode_ListProperties_ComeBackAsCounts_ScalarsInline()
    {
        var tools = new GraphNavigationTools(StoreOver(NodeWithListsGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(ListyId));
        var node = doc.RootElement.GetProperty("node");

        // Scalars are cheap and answer a question without a second call.
        Assert.True(node.GetProperty("properties").GetProperty("isPublic").GetBoolean());
        Assert.Equal("Nav.Squad", node.GetProperty("properties").GetProperty("declaringType").GetString());

        // Lists say how big they are and nothing else.
        var methods = node.GetProperty("collections").GetProperty("methods");
        Assert.Equal(40, methods.GetProperty("count").GetInt32());
        Assert.False(methods.GetProperty("expanded").GetBoolean());
        Assert.False(methods.TryGetProperty("values", out _));
    }

    [Fact]
    public void GetNode_Fields_ExpandsTheOneListThatWasWanted()
    {
        var tools = new GraphNavigationTools(StoreOver(NodeWithListsGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(ListyId, fields: "throws"));
        var collections = doc.RootElement.GetProperty("node").GetProperty("collections");

        Assert.True(collections.GetProperty("throws").GetProperty("expanded").GetBoolean());
        Assert.Equal(2, collections.GetProperty("throws").GetProperty("values").GetArrayLength());

        // And the one that was not asked for stays a count.
        Assert.False(collections.GetProperty("methods").GetProperty("expanded").GetBoolean());
        Assert.Equal(40, collections.GetProperty("methods").GetProperty("count").GetInt32());
    }

    [Fact]
    public void GetNode_AvailableFields_NamesTheListsThisNodeCarries()
    {
        var tools = new GraphNavigationTools(StoreOver(NodeWithListsGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(ListyId));
        var names = doc.RootElement.GetProperty("availableFields").GetProperty("fields")
            .EnumerateArray().Select(e => e.GetString()).ToList();

        Assert.Equal(["methods", "throws"], names);
    }

    [Fact]
    public void GetNode_UnknownField_IsRefusedNamingWhatTheNodeHas()
    {
        var tools = new GraphNavigationTools(StoreOver(NodeWithListsGraph()));

        var message = Assert.Throws<McpException>(() => tools.GetNode(ListyId, fields: "properties")).Message;

        Assert.Contains("methods", message, StringComparison.Ordinal);
        Assert.Contains("throws", message, StringComparison.Ordinal);
    }

    private const string ListyId = "m:Nav.Squad.Advance()";

    /// <summary>A node with both kinds of property: cheap scalars and two lists.</summary>
    private static CodeGraph NodeWithListsGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        var node = new GraphNode { Id = ListyId, Type = NodeType.Method, Name = "Advance" };
        node.SetProperty("isPublic", true);
        node.SetProperty("declaringType", "Nav.Squad");
        node.SetProperty("methods", Enumerable.Range(0, 40).Select(i => $"Member{i}").ToList());
        node.SetProperty("throws", new List<string> { "System.InvalidOperationException", "Nav.SquadException" });
        graph.AddNode(node);
        return graph;
    }

    // ---- Freshness: absence has to mean one thing ------------------------------

    [Fact]
    public void GetNode_OnAStampedGraph_ReportsWhenItWasBuilt_AndNoCaveat()
    {
        // The line span is only true of the source as it stood at build time,
        // so the response says when that was rather than making the caller ask
        // list_graphs separately.
        var graph = WellCoveredGraph();
        graph.BuiltAt = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
        var tools = new GraphNavigationTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero),
            doc.RootElement.GetProperty("builtAt").GetDateTimeOffset());
        Assert.False(doc.RootElement.TryGetProperty("freshnessCaveat", out _));
    }

    [Fact]
    public void GetNode_OnAnUnstampedGraph_SaysAbsenceProvesNothing()
    {
        // Without this, a graph with no build stamp reports no fileWrittenSince
        // on any node — which reads exactly like "every file is unchanged", the
        // reassuring silence the freshness check exists to prevent.
        var graph = WellCoveredGraph();
        graph.BuiltAt = null;
        var tools = new GraphNavigationTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.GetNode(CoveredMethodId));

        Assert.False(doc.RootElement.TryGetProperty("builtAt", out _));
        var caveat = doc.RootElement.GetProperty("freshnessCaveat").GetString()!;
        Assert.Contains("means NOTHING", caveat, StringComparison.Ordinal);
        Assert.Contains("Rebuild", caveat, StringComparison.Ordinal);
    }

    [Fact]
    public void FindNodes_CarriesTheSameStamp()
    {
        // Same warrant, same answer: find_nodes hands back a line per node.
        var graph = MixedKindGraph();
        graph.BuiltAt = null;
        var tools = new GraphNavigationTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.FindNodes(nameContains: "Widget"));

        Assert.True(doc.RootElement.TryGetProperty("freshnessCaveat", out _));
    }

    [Fact]
    public void ListGraphs_ReportsBothBuildStamps()
    {
        // builtAt says when extraction ran; extractorBuiltAt says which extractor
        // ran it. A host serving a published binary can rebuild seconds ago with
        // code days old, and only the second stamp shows that.
        var graph = MixedKindGraph();
        graph.BuiltAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        graph.ExtractorBuiltAt = new DateTimeOffset(2026, 9, 12, 8, 3, 0, TimeSpan.Zero);
        var tools = new GraphStoreTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.ListGraphs());

        var entry = Assert.Single(doc.RootElement.GetProperty("graphs").EnumerateArray());
        Assert.Equal(graph.BuiltAt, entry.GetProperty("builtAt").GetDateTimeOffset());
        Assert.Equal(graph.ExtractorBuiltAt, entry.GetProperty("extractorBuiltAt").GetDateTimeOffset());
    }

    [Fact]
    public void GraphSummary_ReportsBothBuildStamps()
    {
        // Summarize backs build_graph, build_solution, load_graph and
        // graph_summary, so the build response itself now says which extractor
        // answered rather than only that extraction just ran.
        var graph = MixedKindGraph();
        graph.BuiltAt = new DateTimeOffset(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
        graph.ExtractorBuiltAt = new DateTimeOffset(2026, 9, 12, 8, 3, 0, TimeSpan.Zero);
        var tools = new GraphStoreTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.GraphSummary("g"));

        Assert.Equal(graph.BuiltAt, doc.RootElement.GetProperty("builtAt").GetDateTimeOffset());
        Assert.Equal(graph.ExtractorBuiltAt, doc.RootElement.GetProperty("extractorBuiltAt").GetDateTimeOffset());
    }

    [Fact]
    public void ListGraphs_ReportsWhatEachGraphWasBuiltFor()
    {
        // Two graphs of one source that differ only here differ on hundreds of
        // generated-code paths, so list_graphs is where the difference has to show.
        var graph = MixedKindGraph();
        graph.HostArchitecture = "arm64";
        graph.Platform = "x64";
        graph.PlatformFrom = "environment";
        var tools = new GraphStoreTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.ListGraphs());

        var entry = Assert.Single(doc.RootElement.GetProperty("graphs").EnumerateArray());
        Assert.Equal("arm64", entry.GetProperty("hostArchitecture").GetString());
        Assert.Equal("x64", entry.GetProperty("platform").GetString());
        Assert.Equal("environment", entry.GetProperty("platformFrom").GetString());
    }

    [Fact]
    public void GraphSummary_ReportsWhatTheGraphWasBuiltFor()
    {
        // Summarize backs the build responses too, so a build says what it was
        // built for on the first thing a caller sees.
        var graph = MixedKindGraph();
        graph.HostArchitecture = "x64";
        graph.Platform = "ARM64";
        graph.PlatformFrom = "requested";
        var tools = new GraphStoreTools(StoreOver(graph));

        using var doc = JsonDocument.Parse(tools.GraphSummary("g"));

        Assert.Equal("x64", doc.RootElement.GetProperty("hostArchitecture").GetString());
        Assert.Equal("ARM64", doc.RootElement.GetProperty("platform").GetString());
        Assert.Equal("requested", doc.RootElement.GetProperty("platformFrom").GetString());
    }

    private const string CoveredMethodId = "m:Web.Doctrine.Advance()";

    /// <summary>
    /// One method with a single outgoing call, one incoming call, and 118
    /// incoming Covers edges — the real proportions of a well-tested method.
    /// </summary>
    private static CodeGraph WellCoveredGraph()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = CoveredMethodId, Type = NodeType.Method, Name = "Advance" });
        graph.AddNode(new GraphNode { Id = "m:Web.Doctrine.Step()", Type = NodeType.Method, Name = "Step" });
        graph.AddNode(new GraphNode { Id = "m:Web.Caller.Invoke()", Type = NodeType.Method, Name = "Invoke" });

        graph.AddEdge(new GraphEdge { FromId = CoveredMethodId, ToId = "m:Web.Doctrine.Step()", Type = EdgeType.Calls });
        graph.AddEdge(new GraphEdge { FromId = "m:Web.Caller.Invoke()", ToId = CoveredMethodId, Type = EdgeType.Calls });

        for (var i = 0; i < 118; i++)
        {
            var testId = $"m:Web.Tests.DoctrineTests.Case{i}()";
            graph.AddNode(new GraphNode { Id = testId, Type = NodeType.Method, Name = $"Case{i}" });
            graph.AddEdge(new GraphEdge { FromId = testId, ToId = CoveredMethodId, Type = EdgeType.Covers });
        }

        return graph;
    }

    private static GraphStore StoreOver(CodeGraph graph)
    {
        var store = new GraphStore();
        store.Add(graph, source: "test://hand-built", requestedId: "g");
        return store;
    }

    /// <summary>One name across three kinds, plus a node that does not match.</summary>
    private static CodeGraph MixedKindGraph()
    {
        var graph = new CodeGraph();
        graph.AddNode(new GraphNode { Id = "type:Web.Widget", Type = NodeType.Class, Name = "Widget" });
        graph.AddNode(new GraphNode { Id = "m:Web.Widget.Widget()", Type = NodeType.Method, Name = "Widget" });
        graph.AddNode(new GraphNode { Id = "prop:Web.Panel.Widget", Type = NodeType.Property, Name = "Widget" });
        graph.AddNode(new GraphNode { Id = "type:Web.Panel", Type = NodeType.Class, Name = "Panel" });
        return graph;
    }

    private static ExceptionEscapeTools ToolsOver(CodeGraph graph)
    {
        var store = new GraphStore();
        store.Add(graph, source: "test://hand-built", requestedId: "g");
        return new ExceptionEscapeTools(store);
    }

    private static CodeGraph EscapeGraph()
    {
        var graph = new CodeGraph();

        var handler = new GraphNode
        {
            Id = "m:Web.Widget.OnTick(object,System.EventArgs)",
            Type = NodeType.Method,
            Name = "OnTick"
        };
        handler.SetProperty("entryPointKind", "eventHandler");
        handler.SetProperty("project", "Web");
        graph.AddNode(handler);

        var thrower = new GraphNode { Id = "m:Lib.Thrower.Boom()", Type = NodeType.Method, Name = "Boom" };
        thrower.SetProperty("throws", new List<string> { "Lib.FooException" });
        graph.AddNode(thrower);

        var escape = new GraphEdge { FromId = thrower.Id, ToId = handler.Id, Type = EdgeType.Escapes };
        escape.Properties["exceptionType"] = "Lib.FooException";
        escape.Properties["depth"] = 1;
        escape.Properties["path"] = new List<string> { thrower.Id, handler.Id };
        escape.Properties["interceptedBy"] = new List<string> { "m:Web.ShapingMiddleware.InvokeAsync(HttpContext,RequestDelegate)" };
        graph.AddEdge(escape);

        var boundary = new GraphNode
        {
            Id = "m:Web.ShapingMiddleware.InvokeAsync(HttpContext,RequestDelegate)",
            Type = NodeType.Method,
            Name = "InvokeAsync"
        };
        graph.AddNode(boundary);

        return graph;
    }

    [Fact]
    public void ExceptionEscapes_ReturnsTheEnvelopeWithCaveats()
    {
        using var doc = JsonDocument.Parse(ToolsOver(EscapeGraph()).ExceptionEscapes());
        var root = doc.RootElement;

        Assert.Equal(1, root.GetProperty("returned").GetInt32());
        Assert.Equal(1, root.GetProperty("totalMatches").GetInt32());
        Assert.False(root.GetProperty("truncated").GetBoolean());

        var escape = root.GetProperty("escapes").EnumerateArray().Single();
        Assert.Equal("Lib.FooException", escape.GetProperty("exceptionType").GetString());
        Assert.Equal(1, escape.GetProperty("depth").GetInt32());
        Assert.Equal("eventHandler", escape.GetProperty("entryPoint").GetProperty("kind").GetString());
        Assert.Equal("Boom", escape.GetProperty("thrownBy").GetProperty("name").GetString());
        Assert.Equal(2, escape.GetProperty("path").GetArrayLength());

        // A boundary interception reads as disposition plus the resolved boundary.
        Assert.Equal("intercepted", escape.GetProperty("disposition").GetString());
        var boundary = escape.GetProperty("interceptedBy").EnumerateArray().Single();
        Assert.Equal("InvokeAsync", boundary.GetProperty("name").GetString());
        Assert.False(boundary.GetProperty("conditional").GetBoolean());

        // Blind spots are data, not fine print — an empty caveats list would be a lie.
        Assert.NotEmpty(root.GetProperty("caveats").EnumerateArray());
    }

    [Fact]
    public void ExceptionEscapes_RejectsAnUnknownEntryPointKind()
    {
        var ex = Assert.Throws<McpException>(
            () => ToolsOver(EscapeGraph()).ExceptionEscapes(entryPointKind: "clickHandler"));
        Assert.Contains("eventHandler", ex.Message); // the error teaches the valid kinds
    }

    // A pre-analysis graph must refuse, not report "nothing escapes" — silence
    // would be a claim the graph cannot back.
    [Fact]
    public void ExceptionEscapes_RefusesAGraphWithoutEscapeData()
    {
        var stale = new CodeGraph();
        stale.AddNode(new GraphNode { Id = "m:App.T.M()", Type = NodeType.Method, Name = "M" });

        var ex = Assert.Throws<McpException>(() => ToolsOver(stale).ExceptionEscapes());
        Assert.Contains("Rebuild", ex.Message);
    }

    [Fact]
    public void ExceptionEscapes_EmptyResultOnAnalyzedGraphIsAnAnswer()
    {
        // entryPointKind stamps present, no Escapes edges: analyzed, and clean.
        var clean = new CodeGraph();
        var main = new GraphNode { Id = "m:App.Program.Main()", Type = NodeType.Method, Name = "Main" };
        main.SetProperty("entryPointKind", "main");
        clean.AddNode(main);

        using var doc = JsonDocument.Parse(ToolsOver(clean).ExceptionEscapes());

        Assert.Equal(0, doc.RootElement.GetProperty("returned").GetInt32());
        Assert.False(doc.RootElement.GetProperty("truncated").GetBoolean());
    }

    // ---- An escaped id is a near miss, not an absence ---------------------------

    // A generic signature is the id that breaks. Copied out of a response that
    // crossed a markdown or HTML layer, its angle brackets come back as &lt; and
    // &gt;, the exact lookup misses, and "Node not found" reads as "no such
    // thing" — four wasted calls before anyone suspected the brackets.

    [Fact]
    public void GetNode_AcceptsAnEscapedGenericId_AndAnswersAsIfItWereRaw()
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        var raw = tools.GetNode(PaginateId);
        var escaped = tools.GetNode(HtmlEscape(PaginateId));

        // Byte-identical: the escaped spelling is not a lesser answer, it is the
        // same answer. get_node kept its own copy of the refusal and so was the
        // last tool still rejecting this.
        Assert.Equal(raw, escaped);

        using var doc = JsonDocument.Parse(escaped);
        Assert.Equal(PaginateId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void CoveringTests_AcceptsAnEscapedGenericId()
    {
        // The fix has to live at the boundary, not in get_node: a caller pastes
        // an id into whichever tool answers the question they have.
        var tools = new CoverageTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.CoveringTests(HtmlEscape(PaginateId)));

        // Not merely "did not throw": validating the decoded id and then querying
        // the raw one would come back here as an empty, cheerful zero.
        Assert.Equal(1, doc.RootElement.GetProperty("totalMatches").GetInt32());
        Assert.Equal(PagingTestId, doc.RootElement.GetProperty("tests").EnumerateArray()
            .Single().GetProperty("node").GetProperty("id").GetString());

        // And the echoed id is the one that resolved, so the caller learns the
        // spelling that works.
        Assert.Equal(PaginateId, doc.RootElement.GetProperty("methodId").GetString());
    }

    [Fact]
    public void FindPath_AcceptsAnEscapedGenericIdOnBothEnds()
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(
            tools.FindPath(HtmlEscape(PaginateId), HtmlEscape(WindowId)));

        // found=false is this tool's shape for "no path", so a validated-but-
        // unused decode would report an honest-looking lie rather than throw.
        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
        var hop = doc.RootElement.GetProperty("edges").EnumerateArray().Single();
        Assert.Equal(PaginateId, hop.GetProperty("from").GetString());
        Assert.Equal(WindowId, hop.GetProperty("to").GetString());
    }

    [Fact]
    public void AnIdWhoseAmpersandSequenceIsLiteral_StillWinsOverTheDecodedOne()
    {
        // Both spellings exist as separate nodes. Decoding before trying the id
        // as given would hand back the wrong one of the two — silently, with a
        // node in hand and nothing to suggest it was the other node's.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var literal = JsonDocument.Parse(tools.GetNode(LiteralEntityId));
        using var decoded = JsonDocument.Parse(tools.GetNode(DecodedEntityId));

        Assert.Equal(LiteralEntityId, literal.RootElement.GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(DecodedEntityId, decoded.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void DecodingIsOnePassLeftToRight_NotTwo()
    {
        // A string literal that itself contains HTML: its id holds the four
        // characters &lt; verbatim. Escaped for transport that becomes &amp;lt;,
        // and one honest pass puts the id back.
        //
        // Decoding the ampersand FIRST runs the pass twice — &amp;lt; to &lt; to
        // a bare '<' — inventing a bracket the source never had and landing on
        // an id that exists nowhere. Hence: ampersand last.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(HtmlEscape(MarkupLiteralId)));

        Assert.Equal(MarkupLiteralId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void ABareAmpersandIsNotAnEscapeSequence_AndIsLeftAlone()
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(BareAmpersandId));

        Assert.Equal(BareAmpersandId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void WhitespaceInsideAGenericArgumentList_SurvivesNormalisation()
    {
        // Func<TSource, TItem> carries a space after its comma while the enclosing
        // parameter list carries none. Two nodes differ by exactly that space, so
        // a normaliser that trimmed or collapsed whitespace would resolve onto the
        // wrong one instead of failing loudly.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var spaced = JsonDocument.Parse(tools.GetNode(HtmlEscape(PaginateId)));
        using var tight = JsonDocument.Parse(tools.GetNode(HtmlEscape(PaginateIdWithoutTheSpace)));

        Assert.Equal(PaginateId, spaced.RootElement.GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(PaginateIdWithoutTheSpace, tight.RootElement.GetProperty("node").GetProperty("id").GetString());
        Assert.Equal("Paginate", spaced.RootElement.GetProperty("node").GetProperty("name").GetString());
        Assert.Equal("PaginateTwin", tight.RootElement.GetProperty("node").GetProperty("name").GetString());
    }

    [Fact]
    public void AnUnknownEscapedId_IsRefusedInTermsOfTheEscaping()
    {
        // It still fails — decoding is not a licence to invent nodes — but the
        // refusal has to name the escaping, because "Node not found" on a
        // near-miss id is precisely what sent a caller looking in the wrong place.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));
        var missing = "m:Web.Nowhere.Vanish(System.Func<T, T>)";

        var message = Assert.Throws<McpException>(() => tools.GetNode(HtmlEscape(missing))).Message;

        Assert.Contains("escape", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(missing, message, StringComparison.Ordinal);
        Assert.Contains("find_nodes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownPlainId_IsRefusedWithoutBlamingEscaping()
    {
        // The mirror: escaping is named only when escaping was in play, so the
        // hint stays a hint rather than boilerplate on every miss.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        var message = Assert.Throws<McpException>(() => tools.GetNode("m:Web.Nowhere.Vanish()")).Message;

        Assert.DoesNotContain("escape", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("find_nodes", message, StringComparison.Ordinal);
    }

    // ---- The JSON spelling: the one RazorGraph used to put on the wire ---------

    // System.Text.Json's default encoder writes the less-than, greater-than and
    // ampersand signs as backslash-u escapes (backslash-u-003C and the rest). A
    // client that shows the tool result as text rather than parsing it hands
    // the agent those six characters, and an id copied from there arrives with
    // them still in it. Same near miss as &lt;, different costume. ToolResponses
    // now writes them plain; the decode stays for clients that re-escape.
    // (Escapes are spelled out in words here: an earlier copy of this comment
    // was decoded in transit into nonsense.)

    [Fact]
    public void ResponsesCarryAGenericIdPlain_NeitherJsonNorHtmlEscaped()
    {
        // In the raw response text, not just after a parse: the id a caller
        // copies out of get_node is the id get_node takes, nothing to decode.
        // This pinned the escaped form until the encoder changed; flipped on
        // purpose.
        var json = new GraphNavigationTools(StoreOver(EscapedIdGraph())).GetNode(PaginateId);

        Assert.Contains($"\"id\":\"{PaginateId}\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(JsonEscape(PaginateId), json, StringComparison.Ordinal);
        Assert.DoesNotContain("&lt;", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesCarryAnAmpersandPlain()
    {
        var json = new GraphNavigationTools(StoreOver(EscapedIdGraph())).GetNode(BareAmpersandId);

        Assert.Contains($"\"id\":\"{BareAmpersandId}\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ResponsesCarryNonAsciiTextPlain()
    {
        // A literal or a path with accents comes back as written, not as
        // escapes a reader has to decode by eye.
        const string value = "Crème brûlée — déjà vu";
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        graph.AddNode(new GraphNode { Id = "lit:" + value, Type = NodeType.Literal, Name = value, FilePath = "Désserts/Menü.cs" });

        var json = new GraphNavigationTools(StoreOver(graph)).GetNode("lit:" + value);

        Assert.Contains($"\"id\":\"lit:{value}\"", json, StringComparison.Ordinal);
        Assert.Contains("Désserts/Menü.cs", json, StringComparison.Ordinal);
        Assert.DoesNotContain((char)92 + "u", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("html")]
    [InlineData("json")]
    public void GetNode_ResolvesAGenericIdInEverySpelling(string spelling)
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(Spell(PaginateId, spelling)));

        Assert.Equal(PaginateId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("plain")]
    [InlineData("html")]
    [InlineData("json")]
    public void CoveringTests_ResolvesAGenericIdInEverySpelling(string spelling)
    {
        var tools = new CoverageTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.CoveringTests(Spell(PaginateId, spelling)));

        // The covering test, not just "did not throw": a lookup that validated
        // one spelling and queried another would answer an empty zero here.
        Assert.Equal(PagingTestId, doc.RootElement.GetProperty("tests").EnumerateArray()
            .Single().GetProperty("node").GetProperty("id").GetString());
        Assert.Equal(PaginateId, doc.RootElement.GetProperty("methodId").GetString());
    }

    [Fact]
    public void FindPath_ResolvesTheJsonSpellingOnBothEnds()
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.FindPath(JsonEscape(PaginateId), JsonEscape(WindowId)));

        Assert.True(doc.RootElement.GetProperty("found").GetBoolean());
    }

    [Fact]
    public void TheJsonSpellingIsCaseInsensitive()
    {
        // backslash-u003C and backslash-u003c are the same escape; encoders
        // differ on the case of the hex digits.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));
        var lower = JsonEscape(PaginateId).Replace("\\u003C", "\\u003c", StringComparison.Ordinal)
                                          .Replace("\\u003E", "\\u003e", StringComparison.Ordinal);

        using var doc = JsonDocument.Parse(tools.GetNode(lower));

        Assert.Equal(PaginateId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void TheJsonAmpersandDecodesInOnePass_LikeTheHtmlOne()
    {
        // backslash-u0026 followed by "lt;" is an escaped ampersand and then
        // "lt;", and must come back as the four characters &lt; -- not be
        // decoded on into a bracket.
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));

        using var doc = JsonDocument.Parse(tools.GetNode(JsonEscape(MarkupLiteralId)));

        Assert.Equal(MarkupLiteralId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());
    }

    [Fact]
    public void AnUnknownJsonEscapedId_KeepsTheNotFoundRefusal()
    {
        var tools = new GraphNavigationTools(StoreOver(EscapedIdGraph()));
        var missing = "m:Web.Nowhere.Vanish(System.Func<T, T>)";

        var message = Assert.Throws<McpException>(() => tools.GetNode(JsonEscape(missing))).Message;

        Assert.StartsWith("Node not found: ", message, StringComparison.Ordinal);
        Assert.Contains(missing, message, StringComparison.Ordinal);
        Assert.Contains("find_nodes", message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveWith_TriesTheIdAsGivenFirst_ThenItsDecodedSpelling()
    {
        // The method_body tools look ids up in a compilation, not a graph, so
        // they reach the decode through a lookup they supply.
        var known = new Dictionary<string, string> { [PaginateId] = "body" };
        string? Lookup(string id) => known.GetValueOrDefault(id);

        Assert.Equal("body", ToolArguments.ResolveWith(PaginateId, Lookup));
        Assert.Equal("body", ToolArguments.ResolveWith(HtmlEscape(PaginateId), Lookup));
        Assert.Equal("body", ToolArguments.ResolveWith(JsonEscape(PaginateId), Lookup));
        Assert.Null(ToolArguments.ResolveWith("m:Web.Nowhere.Vanish(System.Func\\u003CT, T\\u003E)", Lookup));
    }

    private static string Spell(string id, string spelling) => spelling switch
    {
        "plain" => id,
        "html" => HtmlEscape(id),
        "json" => JsonEscape(id),
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
    };

    /// <summary>
    /// What System.Text.Json's default encoder does to an id's brackets and
    /// ampersand, left as literal text -- the way a client that renders the raw
    /// result shows it.
    /// </summary>
    private static string JsonEscape(string id) => id
        .Replace("&", "\\u0026", StringComparison.Ordinal)
        .Replace("<", "\\u003C", StringComparison.Ordinal)
        .Replace(">", "\\u003E", StringComparison.Ordinal);

    /// <summary>What a markdown or HTML layer does to an id on the way out.</summary>
    private static string HtmlEscape(string id) => id
        .Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal)
        .Replace(">", "&gt;", StringComparison.Ordinal);

    private const string PaginateId =
        "m:RazorGraph.Mcp.ToolResponses.Paginate(System.Collections.Generic.IReadOnlyList<TSource>,int,int,System.Func<TSource, TItem>)";

    /// <summary>The same signature but for the space after the comma.</summary>
    private const string PaginateIdWithoutTheSpace =
        "m:RazorGraph.Mcp.ToolResponses.Paginate(System.Collections.Generic.IReadOnlyList<TSource>,int,int,System.Func<TSource,TItem>)";

    private const string WindowId = "m:RazorGraph.Mcp.ToolResponses.Window(int,int,int)";
    private const string PagingTestId = "m:RazorGraph.Tests.PagingTests.PagesEverything()";

    /// <summary>An id whose ampersand sequence is literal text, not an escape…</summary>
    private const string LiteralEntityId = "lit:a &amp;lt; b";

    /// <summary>…and the node it would be mistaken for if decoding came first.</summary>
    private const string DecodedEntityId = "lit:a &lt; b";

    private const string BareAmpersandId = "lit:Tom & Jerry";

    /// <summary>A string literal that is itself HTML: escaped once, it must decode once.</summary>
    private const string MarkupLiteralId = "lit:use &lt;br&gt; for a line break";

    /// <summary>
    /// Generic signatures, a covering test, and the three ampersand-bearing ids
    /// that separate "decode as a fallback" from "decode first".
    /// </summary>
    private static CodeGraph EscapedIdGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };

        graph.AddNode(new GraphNode { Id = PaginateId, Type = NodeType.Method, Name = "Paginate" });
        graph.AddNode(new GraphNode { Id = PaginateIdWithoutTheSpace, Type = NodeType.Method, Name = "PaginateTwin" });
        graph.AddNode(new GraphNode { Id = WindowId, Type = NodeType.Method, Name = "Window" });
        graph.AddEdge(new GraphEdge { FromId = PaginateId, ToId = WindowId, Type = EdgeType.Calls });

        var test = new GraphNode { Id = PagingTestId, Type = NodeType.Method, Name = "PagesEverything" };
        test.SetProperty("isTest", true);
        graph.AddNode(test);

        var covers = new GraphEdge { FromId = PagingTestId, ToId = PaginateId, Type = EdgeType.Covers };
        covers.Properties["depth"] = 1;
        graph.AddEdge(covers);

        graph.AddNode(new GraphNode { Id = LiteralEntityId, Type = NodeType.Literal, Name = "a &amp;lt; b" });
        graph.AddNode(new GraphNode { Id = DecodedEntityId, Type = NodeType.Literal, Name = "a &lt; b" });
        graph.AddNode(new GraphNode { Id = BareAmpersandId, Type = NodeType.Literal, Name = "Tom & Jerry" });
        graph.AddNode(new GraphNode { Id = MarkupLiteralId, Type = NodeType.Literal, Name = "use &lt;br&gt; for a line break" });

        return graph;
    }
}
