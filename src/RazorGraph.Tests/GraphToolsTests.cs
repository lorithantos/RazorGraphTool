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
}
