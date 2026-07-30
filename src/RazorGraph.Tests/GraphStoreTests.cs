namespace RazorGraph.Tests;

using ModelContextProtocol;
using RazorGraph.Core.Graph;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The registry that replaced the server's single graph slot. The behaviour that
/// matters is that a second build no longer destroys the first, and that a
/// caller who names nothing still gets the obvious graph.
/// </summary>
public class GraphStoreTests
{
    private static CodeGraph GraphWith(params string[] nodeIds)
    {
        var graph = new CodeGraph();
        foreach (var id in nodeIds)
            graph.AddNode(new GraphNode { Id = id, Type = NodeType.Class, Name = id });
        return graph;
    }

    [Fact]
    public void Add_DerivesIdFromSourceFileName()
    {
        var store = new GraphStore();

        var entry = store.Add(GraphWith("a"), @"D:\repos\App\App.sln", requestedId: null);

        Assert.Equal("App", entry.Id);
    }

    [Fact]
    public void Add_StripsGraphSuffixFromSavedFileNames()
    {
        var store = new GraphStore();

        // "Foo.graph.json" is the convention save_graph writes; the id should be Foo.
        var entry = store.Add(GraphWith("a"), @"D:\graphs\Foo.graph.json", requestedId: null);

        Assert.Equal("Foo", entry.Id);
    }

    [Fact]
    public void Add_TwoGraphs_KeepsBoth()
    {
        var store = new GraphStore();

        store.Add(GraphWith("first"), @"D:\a\Alpha.sln", null);
        store.Add(GraphWith("second"), @"D:\b\Beta.sln", null);

        // The regression this whole class exists for: the second build used to
        // overwrite the first.
        Assert.True(store.Require("Alpha").Graph.HasNode("first"));
        Assert.True(store.Require("Beta").Graph.HasNode("second"));
        Assert.Equal(2, store.List().Count);
    }

    [Fact]
    public void Add_CollidingDerivedIds_AreSuffixed()
    {
        var store = new GraphStore();

        var first = store.Add(GraphWith("a"), @"D:\one\App.sln", null);
        var second = store.Add(GraphWith("b"), @"D:\two\App.sln", null);

        Assert.Equal("App", first.Id);
        Assert.Equal("App-2", second.Id);
    }

    [Fact]
    public void Add_ExplicitId_ReplacesInPlace()
    {
        var store = new GraphStore();

        store.Add(GraphWith("old"), @"D:\a\App.sln", "mine");
        store.Add(GraphWith("new"), @"D:\a\App.sln", "mine");

        Assert.Single(store.List());
        Assert.True(store.Require("mine").Graph.HasNode("new"));
    }

    [Fact]
    public void Require_WithoutId_ReturnsMostRecentlyAdded()
    {
        var store = new GraphStore();
        store.Add(GraphWith("first"), @"D:\a\Alpha.sln", null);
        store.Add(GraphWith("second"), @"D:\b\Beta.sln", null);

        Assert.Equal("Beta", store.Require(null).Id);
        Assert.Equal("Beta", store.CurrentId);
    }

    [Fact]
    public void Require_IsCaseInsensitive()
    {
        var store = new GraphStore();
        store.Add(GraphWith("a"), @"D:\a\Alpha.sln", null);

        Assert.Equal("Alpha", store.Require("alpha").Id);
    }

    [Fact]
    public void Require_UnknownId_ListsWhatIsLoaded()
    {
        var store = new GraphStore();
        store.Add(GraphWith("a"), @"D:\a\Alpha.sln", null);

        var ex = Assert.Throws<McpException>(() => store.Require("nope"));

        // A dead-end error should say what the caller could have asked for.
        Assert.Contains("Alpha", ex.Message);
    }

    [Fact]
    public void Require_EmptyStore_Throws()
    {
        Assert.Throws<McpException>(() => new GraphStore().Require(null));
    }

    [Fact]
    public void Remove_DropsGraphAndClearsCurrentWhenItWasDefault()
    {
        var store = new GraphStore();
        store.Add(GraphWith("a"), @"D:\a\Alpha.sln", null);

        Assert.True(store.Remove("Alpha"));
        Assert.False(store.Remove("Alpha"));
        Assert.Null(store.CurrentId);
        Assert.Empty(store.List());
    }

    [Fact]
    public void Remove_NonDefaultGraph_LeavesCurrentAlone()
    {
        var store = new GraphStore();
        store.Add(GraphWith("a"), @"D:\a\Alpha.sln", null);
        store.Add(GraphWith("b"), @"D:\b\Beta.sln", null);

        store.Remove("Alpha");

        Assert.Equal("Beta", store.CurrentId);
    }

    [Fact]
    public void Add_IsSafeUnderConcurrentWriters()
    {
        var store = new GraphStore();

        Parallel.For(0, 50, i => store.Add(GraphWith($"n{i}"), $@"D:\p\Proj{i}.sln", null));

        // Every build gets its own slot; none is lost to a race with another.
        Assert.Equal(50, store.List().Count);
    }
}
