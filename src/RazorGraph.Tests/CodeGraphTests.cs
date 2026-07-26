namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using Xunit;

public class CodeGraphTests
{
    private static GraphNode Node(string id, NodeType type = NodeType.Class) =>
        new() { Id = id, Type = type, Name = id };

    private static GraphEdge Edge(string from, string to, EdgeType type = EdgeType.Calls) =>
        new() { FromId = from, ToId = to, Type = type };

    [Fact]
    public void AddNode_ThenGetNode_ReturnsNode()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));

        Assert.True(graph.HasNode("a"));
        Assert.NotNull(graph.GetNode("a"));
        Assert.Null(graph.GetNode("missing"));
    }

    [Fact]
    public void AddEdge_PopulatesAdjacency()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));
        graph.AddNode(Node("b"));
        graph.AddEdge(Edge("a", "b"));

        Assert.Single(graph.Outgoing("a"));
        Assert.Single(graph.Incoming("b"));
        Assert.Empty(graph.Outgoing("b"));
    }

    [Fact]
    public void AddNode_Upsert_PreservesExistingEdges()
    {
        // Regression: re-adding a node id must not orphan edges from adjacency.
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));
        graph.AddNode(Node("b"));
        graph.AddEdge(Edge("a", "b"));

        graph.AddNode(Node("a", NodeType.PartialView));
        graph.AddNode(Node("b", NodeType.PartialView));

        Assert.Single(graph.Outgoing("a"));
        Assert.Single(graph.Incoming("b"));
        Assert.Equal(NodeType.PartialView, graph.GetNode("a")!.Type);
    }

    [Fact]
    public void Traverse_RespectsEdgeFilter()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b", "c" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("a", "b", EdgeType.Calls));
        graph.AddEdge(Edge("a", "c", EdgeType.Inherits));

        var reached = graph.Traverse("a", new HashSet<EdgeType> { EdgeType.Calls })
            .Select(t => t.Node.Id)
            .ToList();

        Assert.Equal(new[] { "b" }, reached);
    }

    [Fact]
    public void Traverse_RespectsMaxDepth()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b", "c", "d" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("a", "b"));
        graph.AddEdge(Edge("b", "c"));
        graph.AddEdge(Edge("c", "d"));

        var reached = graph.Traverse("a", null, maxDepth: 2).Select(t => t.Node.Id).ToList();

        Assert.Contains("b", reached);
        Assert.Contains("c", reached);
        Assert.DoesNotContain("d", reached);
    }

    [Fact]
    public void Traverse_HandlesCycles()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("a", "b"));
        graph.AddEdge(Edge("b", "a"));

        var reached = graph.Traverse("a", null, maxDepth: 10).Select(t => t.Node.Id).ToList();

        Assert.Equal(new[] { "b" }, reached);
    }

    [Fact]
    public void FindPath_ReturnsPathWhenReachable()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b", "c" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("a", "b"));
        graph.AddEdge(Edge("b", "c"));

        var path = graph.FindPath("a", "c");

        Assert.NotNull(path);
        Assert.Equal(2, path!.Count);
        Assert.Equal("a", path[0].FromId);
        Assert.Equal("c", path[^1].ToId);
    }

    [Fact]
    public void FindPath_ReturnsNullWhenUnreachable()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));
        graph.AddNode(Node("b"));

        Assert.Null(graph.FindPath("a", "b"));
    }
}
