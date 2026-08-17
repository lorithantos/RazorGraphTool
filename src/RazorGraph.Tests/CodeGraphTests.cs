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

    // ---- Annotation edges are a lookup, not a route ---------------------------

    /// <summary>
    /// Two test methods decorated with the same attribute are two hops apart
    /// through it. Following that by default makes find_path -- whose question is
    /// whether two things are related at all -- answer yes for every pair in a
    /// solution, because one attribute node collects hundreds of them.
    /// </summary>
    [Fact]
    public void FindPath_Unfiltered_WillNotRouteThroughAnAttribute()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "m:One", "m:Two" }) graph.AddNode(Node(id, NodeType.Method));
        graph.AddNode(Node("ext:Xunit.FactAttribute", NodeType.ExternalType));
        graph.AddEdge(Edge("m:One", "ext:Xunit.FactAttribute", EdgeType.DecoratedBy));
        graph.AddEdge(Edge("m:Two", "ext:Xunit.FactAttribute", EdgeType.DecoratedBy));

        Assert.Null(graph.FindPath("m:One", "m:Two", edgeFilter: null, TraversalDirection.Both));
    }

    [Fact]
    public void Traverse_Unfiltered_DoesNotReachTheAttribute()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("m:One", NodeType.Method));
        graph.AddNode(Node("ext:Xunit.FactAttribute", NodeType.ExternalType));
        graph.AddEdge(Edge("m:One", "ext:Xunit.FactAttribute", EdgeType.DecoratedBy));

        Assert.Empty(graph.Traverse("m:One"));
    }

    /// <summary>Asking for it by name is the whole point of keeping the edge.</summary>
    [Fact]
    public void Traverse_AskedForByName_StillFollowsTheAttribute()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("m:One", NodeType.Method));
        graph.AddNode(Node("ext:Xunit.FactAttribute", NodeType.ExternalType));
        graph.AddEdge(Edge("m:One", "ext:Xunit.FactAttribute", EdgeType.DecoratedBy));

        var reached = graph
            .Traverse("m:One", new HashSet<EdgeType> { EdgeType.DecoratedBy })
            .Select(t => t.Node.Id)
            .ToList();

        Assert.Equal(new[] { "ext:Xunit.FactAttribute" }, reached);
    }

    /// <summary>
    /// AddEdge accepts an endpoint with no node, so a graph written elsewhere can
    /// arrive holding one. Reporting a route across a node nobody can open is a
    /// worse answer than reporting none.
    /// </summary>
    [Fact]
    public void FindPath_WillNotRouteThroughAnIdThatHasNoNode()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));
        graph.AddNode(Node("c"));
        graph.AddEdge(Edge("a", "ghost"));
        graph.AddEdge(Edge("ghost", "c"));

        Assert.Null(graph.FindPath("a", "c"));
    }

    /// <summary>But a path that ENDS at one is still a path: the caller named it.</summary>
    [Fact]
    public void FindPath_ToAnIdThatHasNoNode_StillAnswers()
    {
        var graph = new CodeGraph();
        graph.AddNode(Node("a"));
        graph.AddEdge(Edge("a", "ghost"));

        var path = graph.FindPath("a", "ghost");

        Assert.NotNull(path);
        Assert.Single(path!);
    }

    // ---- Direction ----------------------------------------------------------

    [Fact]
    public void Traverse_Incoming_FollowsEdgesBackwards()
    {
        // InjectedInto is authored service -> consumer, so "what does this page
        // depend on" is only answerable by walking the edge backwards.
        var graph = new CodeGraph();
        graph.AddNode(Node("svc"));
        graph.AddNode(Node("page"));
        graph.AddEdge(Edge("svc", "page", EdgeType.InjectedInto));

        Assert.Empty(graph.Traverse("page"));

        var incoming = graph.Traverse("page", null, 3, TraversalDirection.Incoming)
            .Select(t => t.Node.Id)
            .ToList();

        Assert.Equal(new[] { "svc" }, incoming);
    }

    [Fact]
    public void Traverse_Both_ReachesEitherWay()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "up", "mid", "down" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("up", "mid"));
        graph.AddEdge(Edge("mid", "down"));

        var reached = graph.Traverse("mid", null, 1, TraversalDirection.Both)
            .Select(t => t.Node.Id)
            .ToList();

        Assert.Contains("up", reached);
        Assert.Contains("down", reached);
    }

    [Fact]
    public void FindPath_Incoming_FindsReversePath()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "a", "b", "c" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("a", "b"));
        graph.AddEdge(Edge("b", "c"));

        Assert.Null(graph.FindPath("c", "a"));

        var path = graph.FindPath("c", "a", edgeFilter: null, TraversalDirection.Incoming);

        Assert.NotNull(path);
        Assert.Equal(2, path!.Count);
    }

    // ---- Transparent (structural) edges --------------------------------------

    [Fact]
    public void Traverse_TransparentEdges_DoNotConsumeDepth()
    {
        // class --Contains--> method --Calls--> callee --Calls--> deeper
        var graph = new CodeGraph();
        foreach (var id in new[] { "class", "method", "callee", "deeper" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("class", "method", EdgeType.Contains));
        graph.AddEdge(Edge("method", "callee"));
        graph.AddEdge(Edge("callee", "deeper"));

        var transparent = new HashSet<EdgeType> { EdgeType.Contains };
        var reached = graph
            .Traverse("class", null, maxDepth: 1, TraversalDirection.Outgoing, transparent)
            .ToList();

        // The regression: charging Contains a hop spent the entire depth budget
        // getting from the class to its own method, so no call was ever reported.
        Assert.Contains(reached, t => t.Node.Id == "method" && t.Depth == 0);
        Assert.Contains(reached, t => t.Node.Id == "callee" && t.Depth == 1);
        Assert.DoesNotContain(reached, t => t.Node.Id == "deeper");
    }

    [Fact]
    public void Traverse_WithoutTransparency_StillChargesEveryEdge()
    {
        var graph = new CodeGraph();
        foreach (var id in new[] { "class", "method", "callee" }) graph.AddNode(Node(id));
        graph.AddEdge(Edge("class", "method", EdgeType.Contains));
        graph.AddEdge(Edge("method", "callee"));

        var reached = graph.Traverse("class", null, maxDepth: 1).Select(t => t.Node.Id).ToList();

        Assert.Equal(new[] { "method" }, reached);
    }
}
