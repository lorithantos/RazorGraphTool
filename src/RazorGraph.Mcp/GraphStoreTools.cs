namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;

/// <summary>
/// Tools that manage the graphs the server holds: build them from source, load
/// and save them as JSON, list, summarize, and drop them.
/// </summary>
[McpServerToolType]
public sealed class GraphStoreTools(GraphStore store)
{
    [McpServerTool(Name = "build_graph")]
    [Description("Build a code graph from one ASP.NET Core Razor project (.csproj), or one project inside a solution. Slow (compiles the project). To graph a whole solution with edges that cross project boundaries, use build_solution instead. Returns a summary with node/edge counts by type.")]
    public async Task<string> BuildGraph(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Project name inside the solution; required when path is a solution")] string? projectName = null,
        [Description("Id to file the result under. Defaults to the file name; reusing an id replaces that graph.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var isSolution = ToolArguments.IsSolution(full);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
            throw new McpException(
                "projectName is required when building a single project from a solution. " +
                "To graph every project at once, call build_solution.");

        await using var builder = new GraphBuilder { IncludeVendorAssets = includeVendor };
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(full, projectName!, ct)
            : await builder.BuildFromProjectAsync(full, ct);

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries);
    }

    [McpServerTool(Name = "build_solution")]
    [Description("Build ONE graph spanning every project in a solution (.sln/.slnx). Unlike build_graph, calls and injections that cross project boundaries become real edges — this is what makes \"which tests cover this method\" and \"what breaks if I change this\" answerable. Adds Project nodes, per-node project attribution, and Covers edges from test methods to the code they exercise. Slower than build_graph: it compiles every project.")]
    public async Task<string> BuildSolution(
        [Description("Absolute path to a .sln or .slnx file")] string path,
        [Description("Id to file the result under. Defaults to the solution file name.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        [Description("Skip test projects: no test Method nodes, no Covers edges — a leaner navigation graph. Coverage tools refuse to answer against it rather than reporting everything uncovered. Default false, so the default graph answers every question.")] bool excludeTests = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");
        if (!ToolArguments.IsSolution(full))
            throw new McpException($"Not a solution file: {full}. Use build_graph for a .csproj.");

        await using var builder = new GraphBuilder
        {
            IncludeVendorAssets = includeVendor,
            ExcludeTestProjects = excludeTests
        };
        var graph = await builder.BuildFromSolutionAllAsync(full, ct);

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries, builder.SkippedTestProjects);
    }

    [McpServerTool(Name = "load_graph")]
    [Description("Load a previously built graph from a JSON file (produced by save_graph or the RazorGraph CLI). Fast; adds it alongside any graphs already loaded.")]
    public async Task<string> LoadGraph(
        [Description("Path to a graph JSON file")] string path,
        [Description("Id to file the result under. Defaults to the file name.")] string? graphId = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var graph = GraphSerializer.FromJson(await File.ReadAllTextAsync(full, ct));
        return Summarize(store.Add(graph, full, graphId));
    }

    [McpServerTool(Name = "save_graph")]
    [Description("Save a loaded graph to a JSON file so future sessions can load_graph instead of rebuilding.")]
    public async Task<string> SaveGraph(
        [Description("Output path for the graph JSON file")] string outputPath,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null,
        CancellationToken ct = default)
    {
        var entry = store.Require(graphId);
        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, GraphSerializer.ToJson(entry.Graph), ct);
        return ToolResponses.ToJson(new { saved = full, graphId = entry.Id, nodes = entry.Graph.Nodes.Count, edges = entry.Graph.Edges.Count });
    }

    [McpServerTool(Name = "list_graphs")]
    [Description("Every graph currently held by the server: id, source, when it was loaded, and its size. Cheap. The 'current' one is what tools use when graphId is omitted.")]
    public string ListGraphs()
    {
        var entries = store.List().Select(e => new
        {
            graphId = e.Id,
            source = e.Source,
            loadedAt = e.LoadedAt,
            nodes = e.Graph.Nodes.Count,
            edges = e.Graph.Edges.Count,
            isCurrent = string.Equals(e.Id, store.CurrentId, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return ToolResponses.ToJson(new { returned = entries.Count, current = store.CurrentId, graphs = entries });
    }

    [McpServerTool(Name = "drop_graph")]
    [Description("Forget a loaded graph, freeing its memory. Returns dropped=false when no graph had that id.")]
    public string DropGraph([Description("Id of the graph to drop")] string graphId) =>
        ToolResponses.ToJson(new { dropped = store.Remove(graphId), graphId });

    [McpServerTool(Name = "graph_summary")]
    [Description("Summary of one graph: source path and node/edge counts by type. Cheap orientation call.")]
    public string GraphSummary([Description(ToolArguments.GraphIdDescription)] string? graphId = null) =>
        Summarize(store.Require(graphId));

    private static string Summarize(
        GraphStore.GraphEntry entry,
        IReadOnlyList<string>? vendorSkips = null,
        IReadOnlyList<string>? testProjectSkips = null)
    {
        var graph = entry.Graph;
        var nodeCounts = graph.Nodes.GroupBy(n => n.Type)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var edgeCounts = graph.Edges.GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var projects = graph.NodesOfType(NodeType.Project).Select(n => n.Name).OrderBy(n => n).ToList();

        return ToolResponses.ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            nodes = graph.Nodes.Count,
            edges = graph.Edges.Count,
            projects = projects.Count > 0 ? projects : null,
            nodeCounts,
            edgeCounts,
            // Present only on build responses that dropped vendor assets: a
            // silent skip would read as "everything was graphed".
            skippedVendorAssets = vendorSkips is { Count: > 0 } ? vendorSkips : null,
            // Same contract for excluded test projects: this graph cannot
            // answer coverage questions, and its summary must say why.
            skippedTestProjects = testProjectSkips is { Count: > 0 } ? testProjectSkips : null
        });
    }
}
