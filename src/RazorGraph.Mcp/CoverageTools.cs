namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;

/// <summary>
/// Tools that answer reachability-from-tests questions over Covers edges:
/// which tests exercise a method, what a test exercises, and what nothing
/// reaches at all.
/// </summary>
[McpServerToolType]
public sealed class CoverageTools(GraphStore store)
{
    [McpServerTool(Name = "covering_tests")]
    [Description("Test methods that exercise a given production method, nearest first. Requires a graph built with build_solution — coverage edges cross a project boundary and cannot exist in a single-project graph.")]
    public string CoveringTests(
        [Description("Id of a Method node in production code")] string methodId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, methodId);

        var items = RequireCoverage(() => new GraphQuery(graph).GetCoveringTests(methodId)
            .Select(t => new { depth = t.Depth, node = ToolResponses.NodeSummary(t.Test) })
            .ToList());
        return ToolResponses.ToJson(new { methodId, returned = items.Count, tests = items });
    }

    [McpServerTool(Name = "covered_methods")]
    [Description("Production methods a given test method exercises, nearest first. depth=1 means the test calls it directly.")]
    public string CoveredMethods(
        [Description("Id of a test Method node")] string testId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, testId);

        var items = RequireCoverage(() => new GraphQuery(graph).GetCoveredMethods(testId)
            .Select(t => new { depth = t.Depth, node = ToolResponses.NodeSummary(t.Method) })
            .ToList());
        return ToolResponses.ToJson(new { testId, returned = items.Count, methods = items });
    }

    [McpServerTool(Name = "uncovered_methods")]
    [Description("Methods that no test reaches. Pass a project to scope the question to one assembly, which is almost always what you want. This is reachability through the call graph, not runtime coverage — a method listed here is unreached by any test's call chain, including setup done in test-class lifecycle hooks.")]
    public string UncoveredMethods(
        [Description("Project name to restrict to (e.g. the production assembly)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = RequireCoverage(() => new GraphQuery(graph).FindUncoveredMethods(project).ToList());
        var page = all.Take(Math.Max(1, limit)).Select(ToolResponses.NodeSummary).ToList();
        return ToolResponses.ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, methods = page });
    }

    /// <summary>
    /// The query layer refuses coverage questions a test-less graph cannot
    /// answer (built single-project, or with excludeTests). Re-shaped as an
    /// McpException so the caller sees the refusal, same as the escapes guard.
    /// </summary>
    private static T RequireCoverage<T>(Func<T> query)
    {
        try { return query(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }
}
