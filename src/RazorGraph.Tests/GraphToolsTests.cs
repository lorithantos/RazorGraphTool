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
