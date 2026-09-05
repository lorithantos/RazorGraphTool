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
internal sealed class CoverageTools(GraphStore store)
{
    [McpServerTool(Name = "covering_tests")]
    [Description("Test methods that exercise a given production method, nearest first. Reachability through Calls edges, widened through interface dispatch: a test that calls I.M reaches every in-solution implementation of I.M one hop further on. Requires a graph built with build_solution — coverage edges cross a project boundary and cannot exist in a single-project graph. An empty list carries a 'caveat' naming what the walk cannot follow, so zero is never mistaken for proof of absence.")]
    public string CoveringTests(
        [Description("Id of a Method node in production code")] string methodId,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, methodId);

        var items = RequireCoverage(() => new GraphQuery(graph).GetCoveringTests(methodId)
            .Select(t => new { depth = t.Depth, node = ToolResponses.NodeSummary(t.Test) })
            .ToList());
        return ToolResponses.ToJson(new
        {
            methodId,
            returned = items.Count,
            tests = items,
            caveat = items.Count == 0 ? UnreachedCaveat : null
        });
    }

    /// <summary>
    /// What an empty coverage answer must say. A zero used to read as "untested",
    /// and on MVVM code it was an argument for writing tests that already existed:
    /// the walk had stopped one dispatch step short. Interface dispatch is followed
    /// now; the steps still not followed are named, so the reader can ask the
    /// right question instead of trusting the empty one.
    /// </summary>
    internal const string UnreachedCaveat =
        "No test's call chain reaches this method through Calls edges or interface dispatch. That is not proof it is untested: property accessor bodies (including source-generated setters), virtual overrides reached through a base-class call, stored-and-forwarded delegates, and reflection are not followed. Read the method body for those before treating this as a target for new tests.";

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
    [Description("Methods that no test reaches. Pass a project to scope the question to one assembly, which is almost always what you want. This is reachability through the call graph, not runtime coverage — a method listed here is unreached by any test's call chain, including setup done in test-class lifecycle hooks and interface dispatch. Steps the walk cannot follow (property accessor bodies, base-class virtual dispatch, stored delegates, reflection) can list a method here that tests do exercise; the list is a set of candidates to read, not a verdict.")]
    public string UncoveredMethods(
        [Description("Project name to restrict to (e.g. the production assembly)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = RequireCoverage(() => new GraphQuery(graph).FindUncoveredMethods(project).ToList());
        var page = all.Take(Math.Max(1, limit)).Select(n => ToolResponses.NodeSummary(n)).ToList();
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
