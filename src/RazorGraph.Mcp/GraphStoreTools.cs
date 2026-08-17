namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;
using RazorGraph.Lua;

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

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries,
            unboundShapes: builder.UnboundShapes, unresolvedAttributes: builder.UnresolvedAttributes);
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

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries, builder.SkippedTestProjects,
            builder.UnboundShapes, unresolvedAttributes: builder.UnresolvedAttributes);
    }

    [McpServerTool(Name = "build_lua")]
    [Description("Build a graph from a directory of Lua source. Unlike build_graph there is no compile step: the host environment is detected from the tree (Info.lua → Lightroom, .rockspec → LuaRocks, else plain path conventions) and files are parsed directly, so it is fast. Modules and module references are foreign kinds ('luaModule', 'requires') selectable by name; functions are ordinary Method nodes with Contains edges, so normal navigation works. The response reports the detected host, and counts resolved / external / unresolved references separately — external means a host or standard-library module outside the tree, which is not a failure.")]
    public string BuildLua(
        [Description("Absolute path to a directory containing Lua source")] string path,
        [Description("Id to file the result under. Defaults to the directory name.")] string? graphId = null,
        [Description("How many unresolved references to list individually; the count is always reported. Default 20.")] int unresolvedLimit = 20,
        [Description("Also graph vendor code such as an SDK's own sample plugins (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when reading the vendor's examples rather than your own code.")] bool includeVendor = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new McpException($"Directory not found: {full}");

        var (graph, report) = new LuaGraphBuilder { IncludeVendor = includeVendor }.Build(full);
        var entry = store.Add(graph, full, graphId ?? new DirectoryInfo(full).Name);

        var listed = report.UnresolvedReferences.Take(Math.Max(0, unresolvedLimit)).ToList();

        return ToolResponses.ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            // Always present: a misdetected host uses the wrong reference function
            // and yields a graph that is empty for a reason nothing else reveals.
            host = report.HostName,
            hostEvidence = report.HostEvidence,
            nodes = graph.Nodes.Count,
            edges = graph.Edges.Count,
            modules = report.Modules,
            functions = report.Functions,
            references = new
            {
                resolved = report.ResolvedReferences,
                external = report.ExternalReferences,
                unresolved = report.UnresolvedReferences.Count
            },
            // Listed, not merely counted: these are the edges the graph does not
            // have, and a bare number cannot be acted on.
            unresolvedReferences = listed.Count > 0 ? listed : null,
            unresolvedTruncated = report.UnresolvedReferences.Count > listed.Count,
            parseFailures = report.ParseFailures.Count > 0 ? report.ParseFailures : null,
            // Set when the host has no module-reference mechanism at all, so that
            // an empty module graph reads as a property of the host rather than a
            // gap in extraction.
            structuralCaveat = report.StructuralCaveat,
            // Same contract as build_solution's skippedVendorAssets: a silent skip
            // would read as "that is all the code there is".
            skippedVendorFiles = report.SkippedVendorFiles.Count > 0 ? report.SkippedVendorFiles.Count : (int?)null
        });
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

        var read = GraphSerializer.Read(await File.ReadAllTextAsync(full, ct));
        return Summarize(store.Add(read.Graph, full, graphId), format: read.Format);
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
        GraphOutput.Prepare(full);
        await File.WriteAllTextAsync(full, GraphSerializer.ToJson(entry.Graph), ct);

        // Present only when there is something to act on, so its presence IS the signal. An
        // always-there field reading "nothing to do" is one the reader learns to skip.
        return ToolResponses.ToJson(new
        {
            saved = full,
            graphId = entry.Id,
            nodes = entry.Graph.Nodes.Count,
            edges = entry.Graph.Edges.Count,
            gitAdvice = GraphOutput.GitAdvice(full),
        });
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
        IReadOnlyList<string>? testProjectSkips = null,
        IReadOnlyList<string>? unboundShapes = null,
        GraphFormatAssessment? format = null,
        IReadOnlyList<string>? unresolvedAttributes = null)
    {
        var graph = entry.Graph;
        // Grouped by display kind, so a foreign vocabulary is censused under its
        // own names. Collapsing it into one "Unknown" bucket would hide both what
        // is in the graph and how much of it this build cannot reason about.
        var nodeCounts = graph.Nodes.GroupBy(n => n.DisplayType)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        var edgeCounts = graph.Edges.GroupBy(e => e.DisplayType)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var projects = graph.NodesOfType(NodeType.Project).Select(n => n.Name).OrderBy(n => n).ToList();

        return ToolResponses.ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            // Present on load responses: which format the file on disk carried.
            // A freshly built graph is this build's format by construction, so
            // only a load has something to report.
            formatVersion = format?.Display,
            formatCaveat = format?.Caveat,
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
            skippedTestProjects = testProjectSkips is { Count: > 0 } ? testProjectSkips : null,
            // Shape names nothing binds. OrchardCore throws when a shape resolves
            // to no binding, so each is a runtime failure on whatever path
            // produces it -- a finding, and findings ride the build response
            // rather than waiting to be queried for.
            unboundShapes = unboundShapes is { Count: > 0 } ? unboundShapes : null,
            // Attribute types C# could not bind. It resolves them at compile time,
            // so this is only ever non-empty when the compilation had errors --
            // which devalues every answer in the graph, not only these edges.
            unresolvedAttributes = unresolvedAttributes is { Count: > 0 } ? unresolvedAttributes : null
        });
    }
}
