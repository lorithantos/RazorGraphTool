namespace RazorGraph.Mcp;

using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// Tools that look inside method bodies: the deep-nesting report over stamped
/// bodyDepth, and the two compile-based tools that graph one body and prove
/// two bodies flow-equivalent.
/// </summary>
[McpServerToolType]
internal sealed class MethodBodyTools(GraphStore store)
{
    [McpServerTool(Name = "deep_methods")]
    [Description("Methods whose body nests control flow at least minDepth levels deep — the deep-nesting (christmas-tree) report, deepest first. bodyDepth is syntactic nesting stamped at build time; expression-bodied and flat methods never appear.")]
    public string DeepMethods(
        [Description("Minimum nesting depth to report (e.g. 4)")] int minDepth,
        [Description("Project name to restrict to")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = new GraphQuery(graph).FindDeepMethods(minDepth, project).ToList();
        var page = all.Take(Math.Max(1, limit))
            .Select(m => new { depth = m.GetProperty<int>("bodyDepth"), node = ToolResponses.NodeSummary(m) })
            .ToList();
        return ToolResponses.ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, methods = page });
    }

    [McpServerTool(Name = "method_body_graph")]
    [Description("The graph INSIDE one method: control-flow basic blocks with branch edges, structural regions (try/finally, lifetimes), and every call site anchored to its block with line and guard depth (conditions/loops that must be entered for it to run). Call targets use the same m: ids as the main graph. Slow — compiles the project; a serialized graph is not enough.")]
    public async Task<string> MethodBodyGraph(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Method node id (m:...) whose body to graph")] string methodId,
        [Description("Project name inside the solution; narrows the compile when path is a solution")] string? projectName = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        await LoadCompilationAsync(roslyn, full, projectName, ct);

        var body = roslyn.GetMethodBodyGraph(methodId)
            ?? throw new McpException(
                $"No body graph for '{methodId}': id not found in the compilation, or the method has no body.");
        return ToolResponses.ToJson(body);
    }

    [McpServerTool(Name = "method_body_diff")]
    [Description("Prove two method bodies flow-equivalent, or report precisely why not: bisimulation over control-flow blocks comparing operations, calls, canonicalized branch conditions (guard inversion folds away), and exception-region context. Conservative by design — a renamed local reports different; a semantically wrong change is never blessed. Compare a method against another in the same compilation (againstMethodId) or against a saved body-graph JSON (baselinePath). Slow — compiles the project.")]
    public async Task<string> MethodBodyDiff(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Method node id (m:...) — the right side of the comparison")] string methodId,
        [Description("Another method id in the same compilation to compare against (left side)")] string? againstMethodId = null,
        [Description("Body-graph JSON file saved earlier (left side)")] string? baselinePath = null,
        [Description("Project name inside the solution; narrows the compile when path is a solution")] string? projectName = null,
        CancellationToken ct = default)
    {
        if ((againstMethodId == null) == (baselinePath == null))
            throw new McpException("Exactly one of againstMethodId or baselinePath is required.");

        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        await LoadCompilationAsync(roslyn, full, projectName, ct);

        var right = roslyn.GetMethodBodyGraph(methodId)
            ?? throw new McpException($"No body graph for '{methodId}'.");

        BodyGraph left;
        if (againstMethodId != null)
        {
            left = roslyn.GetMethodBodyGraph(againstMethodId)
                ?? throw new McpException($"No body graph for '{againstMethodId}'.");
        }
        else
        {
            var baselineFull = Path.GetFullPath(baselinePath!);
            if (!File.Exists(baselineFull)) throw new McpException($"Baseline file not found: {baselineFull}");
            left = JsonSerializer.Deserialize<BodyGraph>(await File.ReadAllTextAsync(baselineFull, ct), ToolResponses.Json)
                ?? throw new McpException($"Baseline file is not a body graph: {baselineFull}");
        }

        return ToolResponses.ToJson(BodyGraphComparer.Compare(left, right));
    }

    /// <summary>
    /// Both compile-based tools load the same three ways: whole solution, one
    /// project of a solution, or a lone project. This dispatch lived twice,
    /// byte-identical, before the tools moved here.
    /// </summary>
    private static async Task LoadCompilationAsync(
        RoslynExtractor roslyn, string fullPath, string? projectName, CancellationToken ct)
    {
        if (ToolArguments.IsSolution(fullPath))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(fullPath, ct: ct);
            else
                await roslyn.LoadSolutionAsync(fullPath, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(fullPath, ct);
        }
    }
}
