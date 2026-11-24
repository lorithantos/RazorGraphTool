namespace RazorGraph.Tests;

using System.CommandLine;
using System.Text.Json;
using RazorGraph.Cli;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;
using Xunit;

/// <summary>
/// The query command as a caller meets it: a graph file on disk, arguments, an
/// exit code and what was printed. These pin the places the CLI used to lag its
/// MCP twin.
/// </summary>
/// <remarks>
/// QueryCommand writes to Console rather than to the invocation's output, so
/// these tests swap Console.Out and Console.Error. That is process-wide state,
/// hence a collection that runs with nothing else alongside it.
/// </remarks>
[Collection(CliConsoleCollection.Name)]
public sealed class CliQueryCommandTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("rg-cli-query-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---- A refusal is a failure, whichever coverage question was refused ---------

    [Theory]
    [InlineData("--uncovered")]
    [InlineData("--covering-tests")]
    [InlineData("--covered-methods")]
    public void ACoverageRefusalOnATestlessGraph_ExitsNonZero(string mode)
    {
        var path = Save(TestlessGraph());
        string[] args = mode == "--uncovered"
            ? ["query", path, mode]
            : ["query", path, "--id", PlainMethodId, mode];

        var run = Run(args);

        // A script reads the exit code, not the prose: a refusal that exits 0
        // reads as an answer.
        Assert.Equal(1, run.Exit);
        Assert.Contains("needs a graph containing test methods", run.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--uncovered")]
    [InlineData("--covering-tests")]
    [InlineData("--covered-methods")]
    public void ACoverageRefusalOnATestlessGraph_ExitsNonZero_InJsonToo(string mode)
    {
        var path = Save(TestlessGraph());
        string[] args = mode == "--uncovered"
            ? ["query", path, mode, "--json"]
            : ["query", path, "--id", PlainMethodId, mode, "--json"];

        var run = Run(args);

        Assert.Equal(1, run.Exit);
        using var doc = JsonDocument.Parse(run.Output);
        Assert.Contains("needs a graph containing test methods",
            doc.RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // ---- An escaped id resolves, by the rule the MCP tools use ------------------------

    [Theory]
    [InlineData("plain")]
    [InlineData("html")]
    [InlineData("json")]
    public void NodeReport_ResolvesAGenericIdInEverySpelling(string spelling)
    {
        var path = Save(GenericIdGraph());

        var run = Run("query", path, "--id", Spell(GenericId, spelling), "--covering-tests", "--json");

        Assert.Equal(0, run.Exit);
        using var doc = JsonDocument.Parse(run.Output);
        Assert.Equal(GenericId, doc.RootElement.GetProperty("node").GetProperty("id").GetString());

        // The covering test, not just the node: a report that resolved one
        // spelling and queried with the other would answer an empty list here.
        var test = Assert.Single(doc.RootElement.GetProperty("coveringTests").EnumerateArray());
        Assert.Equal(GenericTestId, test.GetProperty("test").GetProperty("id").GetString());
    }

    [Fact]
    public void NodeReport_AnUnknownEscapedId_IsStillNotFound()
    {
        var path = Save(GenericIdGraph());

        var run = Run("query", path, "--id", Spell("m:Web.Nowhere.Vanish(System.Func<T, T>)", "html"));

        Assert.Equal(1, run.Exit);
        Assert.Contains("Node not found: ", run.Output, StringComparison.Ordinal);
    }

    // ---- A component's empty answer carries the caveat page_context/render_tree do --

    [Theory]
    [InlineData(RoutableComponentId)]
    [InlineData(ChildComponentId)]
    public void RenderTree_OnABlazorComponent_CarriesTheRenderTreeCaveat(string componentId)
    {
        var path = Save(PagesAndComponentsGraph());

        var json = Run("query", path, "--id", componentId, "--render-tree", "--json");
        var text = Run("query", path, "--id", componentId, "--render-tree");

        Assert.Equal(0, json.Exit);
        using var doc = JsonDocument.Parse(json.Output);
        Assert.Equal(0, doc.RootElement.GetProperty("renderTree").GetArrayLength());
        // Compared with the query's own text, not a copy: the MCP tool reads it
        // from the same place.
        Assert.Equal(GraphQuery.ComponentChildrenCaveat,
            doc.RootElement.GetProperty("renderTreeCaveat").GetString());

        Assert.Equal(0, text.Exit);
        Assert.Contains(GraphQuery.ComponentChildrenCaveat, text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void Context_OnABlazorComponent_CarriesThePageContextCaveat()
    {
        var path = Save(PagesAndComponentsGraph());

        var json = Run("query", path, "--id", RoutableComponentId, "--context", "--json");
        var text = Run("query", path, "--id", RoutableComponentId, "--context");

        Assert.Equal(0, json.Exit);
        using var doc = JsonDocument.Parse(json.Output);
        Assert.Equal(GraphQuery.ComponentContextCaveat,
            doc.RootElement.GetProperty("pageContext").GetProperty("caveat").GetString());

        Assert.Equal(0, text.Exit);
        Assert.Contains(GraphQuery.ComponentContextCaveat, text.Output, StringComparison.Ordinal);
    }

    [Fact]
    public void ACshtmlPage_AnswersWithoutACaveat()
    {
        var path = Save(PagesAndComponentsGraph());

        var json = Run("query", path, "--id", CshtmlPageId, "--render-tree", "--context", "--json");
        var text = Run("query", path, "--id", CshtmlPageId, "--render-tree", "--context");

        Assert.Equal(0, json.Exit);
        using var doc = JsonDocument.Parse(json.Output);
        // Its render tree is still there, and the caveats are absent rather
        // than null: a Razor Pages envelope is the shape it always was.
        Assert.Equal(1, doc.RootElement.GetProperty("renderTree").GetArrayLength());
        Assert.False(doc.RootElement.TryGetProperty("renderTreeCaveat", out _));
        Assert.False(doc.RootElement.GetProperty("pageContext").TryGetProperty("caveat", out _));

        Assert.Equal(0, text.Exit);
        Assert.Contains("--- Render Tree ---", text.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("caveat", text.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ACshtmlPartial_StillGetsNoRenderTreeSection()
    {
        // Lifting the RazorPage gate for components must not open it for every
        // partial view.
        var path = Save(PagesAndComponentsGraph());

        var json = Run("query", path, "--id", CshtmlPartialId, "--render-tree", "--json");

        using var doc = JsonDocument.Parse(json.Output);
        Assert.False(doc.RootElement.TryGetProperty("renderTree", out _));
        Assert.False(doc.RootElement.TryGetProperty("renderTreeCaveat", out _));
    }

    // ---- Helpers --------------------------------------------------------------------

    private const string CshtmlPageId = "page:Pages/Index.cshtml";
    private const string CshtmlLayoutId = "page:Pages/Shared/_Layout.cshtml";
    private const string CshtmlPartialId = "page:Pages/Shared/_Card.cshtml";
    private const string RoutableComponentId = "page:Components/Pages/Counter.razor";
    private const string ChildComponentId = "page:Components/GreetingPanel.razor";

    /// <summary>
    /// A .cshtml page with a layout, a .cshtml partial, a routable component
    /// (@page, so a RazorPage node) and a child component (no @page, so a
    /// PartialView node) -- the two shapes a .razor file takes in the graph.
    /// </summary>
    private static CodeGraph PagesAndComponentsGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        graph.AddNode(new GraphNode { Id = CshtmlPageId, Type = NodeType.RazorPage, Name = "Index", FilePath = "Pages/Index.cshtml" });
        graph.AddNode(new GraphNode { Id = CshtmlLayoutId, Type = NodeType.Layout, Name = "_Layout", FilePath = "Pages/Shared/_Layout.cshtml" });
        graph.AddEdge(new GraphEdge { FromId = CshtmlPageId, ToId = CshtmlLayoutId, Type = EdgeType.UsesLayout });
        graph.AddNode(new GraphNode { Id = CshtmlPartialId, Type = NodeType.PartialView, Name = "_Card", FilePath = "Pages/Shared/_Card.cshtml" });
        graph.AddNode(new GraphNode { Id = RoutableComponentId, Type = NodeType.RazorPage, Name = "Counter", FilePath = "Components/Pages/Counter.razor" });
        graph.AddNode(new GraphNode { Id = ChildComponentId, Type = NodeType.PartialView, Name = "GreetingPanel", FilePath = "Components/GreetingPanel.razor" });
        return graph;
    }

    private const string PlainMethodId = "m:Web.Plain.DoWork()";

    private const string GenericId =
        "m:Web.Paging.Paginate(System.Collections.Generic.IReadOnlyList<TSource>,int,int,System.Func<TSource, TItem>)";

    private const string GenericTestId = "m:Web.Tests.PagingTests.PagesEverything()";

    /// <summary>A generic method and the test that covers it.</summary>
    private static CodeGraph GenericIdGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        graph.AddNode(new GraphNode { Id = GenericId, Type = NodeType.Method, Name = "Paginate" });

        var test = new GraphNode { Id = GenericTestId, Type = NodeType.Method, Name = "PagesEverything" };
        test.SetProperty("isTest", true);
        graph.AddNode(test);

        var covers = new GraphEdge { FromId = GenericTestId, ToId = GenericId, Type = EdgeType.Covers };
        covers.Properties["depth"] = 1;
        graph.AddEdge(covers);
        return graph;
    }

    /// <summary>
    /// The id as an HTML layer or System.Text.Json's default encoder leaves it,
    /// as literal text. The JSON escape is built from char 92 so this file holds
    /// no backslash-u sequence for anything to decode in transit.
    /// </summary>
    private static string Spell(string id, string spelling) => spelling switch
    {
        "plain" => id,
        "html" => id.Replace("&", "&amp;", StringComparison.Ordinal)
                    .Replace("<", "&lt;", StringComparison.Ordinal)
                    .Replace(">", "&gt;", StringComparison.Ordinal),
        "json" => id.Replace("&", (char)92 + "u0026", StringComparison.Ordinal)
                    .Replace("<", (char)92 + "u003C", StringComparison.Ordinal)
                    .Replace(">", (char)92 + "u003E", StringComparison.Ordinal),
        _ => throw new ArgumentOutOfRangeException(nameof(spelling), spelling, null),
    };

    /// <summary>Methods, none of them a test: every coverage question must be refused.</summary>
    private static CodeGraph TestlessGraph()
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };
        graph.AddNode(new GraphNode { Id = PlainMethodId, Type = NodeType.Method, Name = "DoWork" });
        return graph;
    }

    private string Save(CodeGraph graph)
    {
        var path = Path.Combine(_dir, $"{Guid.NewGuid():N}.json");
        File.WriteAllText(path, GraphSerializer.ToJson(graph));
        return path;
    }

    private sealed record CliRun(int Exit, string Output, string Error);

    private static CliRun Run(params string[] args)
    {
        var originalOut = Console.Out;
        var originalError = Console.Error;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Console.SetOut(output);
        Console.SetError(error);
        try
        {
            var exit = CliRoot.Create().Parse(args)
                .Invoke(new InvocationConfiguration { Output = output, Error = error });
            return new CliRun(exit, output.ToString(), error.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }
}

/// <summary>Tests that swap the process-wide Console writers, run alone.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CliConsoleCollection
{
    public const string Name = "CLI console";
}
