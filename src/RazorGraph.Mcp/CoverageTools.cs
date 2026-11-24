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
    [Description("Test methods that exercise a given production method, nearest first — depth 1 means a test calls it directly. Reachability through Calls edges, widened through BOTH dispatch mechanisms: a test that calls I.M reaches every in-solution implementation, and a test that calls an abstract or virtual declaration reaches every in-solution override. A property the chain reads or writes reaches what its accessors call, and a type it touches reaches what its static initialization calls. Paged: a well-covered method has hundreds of tests and the count plus the nearest few is usually the whole answer, so read totalMatches and depthCounts before paging. Pass offset=nextOffset to continue; nextOffset is absent when there is no more. Requires a graph built with build_solution — coverage edges cross a project boundary and cannot exist in a single-project graph. An empty list carries a 'caveat' naming what the walk cannot follow, so zero is never mistaken for proof of absence.")]
    public string CoveringTests(
        [Description("Id of a Method node in production code")] string methodId,
        [Description("Max tests to return (default 25). The count and the depth histogram describe the whole set whatever this is.")] int limit = 25,
        [Description("Where to start, for continuation. Pass the nextOffset from the previous page; its absence there means you have seen everything.")] int offset = 0,
        [Description("Return only tests reached at this depth. 1 is direct exercise, which is usually the question; the deep rows are blast radius. depthCounts names the depths this method actually has.")] int? depth = null,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        methodId = ToolArguments.RequireNode(graph, methodId);

        var all = RequireCoverage(() => new GraphQuery(graph).GetCoveringTests(methodId).ToList());

        // The histogram describes the WHOLE set, never the filtered or paged
        // view: it is what a caller reads to decide whether to page at all, so
        // narrowing it with the narrowing argument would answer the question
        // "how much is there" with "how much did you ask for".
        var depthCounts = all.GroupBy(t => t.Depth)
            .OrderBy(g => g.Key)
            .Select(g => new { depth = g.Key, count = g.Count() })
            .ToList();

        if (depth is { } wanted)
        {
            if (!all.Any(t => t.Depth == wanted))
            {
                throw new McpException(
                    $"No test reaches this method at depth {wanted}. Depths present: "
                    + (depthCounts.Count == 0 ? "none" : string.Join(", ", depthCounts.Select(d => d.depth))) + ".");
            }
            all = all.Where(t => t.Depth == wanted).ToList();
        }

        var (window, tests) = ToolResponses.Paginate(all, offset, limit,
            t => new { depth = t.Depth, node = ToolResponses.NodeSummary(t.Test) });

        return ToolResponses.ToJson(new
        {
            methodId,
            returned = window.Returned,
            totalMatches = window.TotalMatches,
            offset = window.Offset,
            truncated = window.Truncated,
            nextOffset = window.NextOffset,
            depthCounts,
            filteredToDepth = depth,
            tests,
            caveat = all.Count == 0 ? UnreachedCaveat : null
        });
    }

    /// <summary>
    /// What an empty coverage answer must say. A zero used to read as "untested",
    /// and on MVVM code it was an argument for writing tests that already existed:
    /// the walk had stopped one dispatch step short. BOTH dispatch mechanisms are
    /// followed now — interface since 2026-09-04, class overrides since
    /// 2026-09-05 — and so are accessor bodies and static initialization, since
    /// 2026-11-23. Source-generated accessor bodies ([ObservableProperty] setters)
    /// are walked like any other: SourceGeneratedCodeTests pins it, so the caveat
    /// stopped hedging on them on 2026-11-24. The steps still not followed are named, so the reader can ask
    /// the right question instead of trusting the empty one.
    /// </summary>
    internal const string UnreachedCaveat =
        "No test's call chain reaches this method through Calls edges, interface dispatch, a base-class override, the accessor bodies of a property the chain reads or writes, or the static initialization of a type it touches. That is not proof it is untested: stored-and-forwarded delegates and reflection are not followed. Read the method body for those before treating this as a target for new tests.";

    [McpServerTool(Name = "covered_methods")]
    [Description("Production methods a given test method exercises, nearest first. depth=1 means the test calls it directly. Paged: one test can reach hundreds of methods through the full call closure, so read totalMatches and depthCounts first, and pass offset=nextOffset to continue. nextOffset is absent when there is no more.")]
    public string CoveredMethods(
        [Description("Id of a test Method node")] string testId,
        [Description("Max methods to return (default 25). The count and the depth histogram describe the whole set whatever this is.")] int limit = 25,
        [Description("Where to start, for continuation. Pass the nextOffset from the previous page.")] int offset = 0,
        [Description("Return only methods reached at this depth. 1 is what the test calls directly. depthCounts names the depths this test actually reaches.")] int? depth = null,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        testId = ToolArguments.RequireNode(graph, testId);

        var all = RequireCoverage(() => new GraphQuery(graph).GetCoveredMethods(testId).ToList());

        var depthCounts = all.GroupBy(t => t.Depth)
            .OrderBy(g => g.Key)
            .Select(g => new { depth = g.Key, count = g.Count() })
            .ToList();

        if (depth is { } wanted)
        {
            if (!all.Any(t => t.Depth == wanted))
            {
                throw new McpException(
                    $"This test reaches nothing at depth {wanted}. Depths present: "
                    + (depthCounts.Count == 0 ? "none" : string.Join(", ", depthCounts.Select(d => d.depth))) + ".");
            }
            all = all.Where(t => t.Depth == wanted).ToList();
        }

        var (window, methods) = ToolResponses.Paginate(all, offset, limit,
            t => new { depth = t.Depth, node = ToolResponses.NodeSummary(t.Method) });

        return ToolResponses.ToJson(new
        {
            testId,
            returned = window.Returned,
            totalMatches = window.TotalMatches,
            offset = window.Offset,
            truncated = window.Truncated,
            nextOffset = window.NextOffset,
            depthCounts,
            filteredToDepth = depth,
            methods
        });
    }

    [McpServerTool(Name = "uncovered_methods")]
    [Description("Methods that no test reaches, ordered by id. Pass a project to scope the question to one assembly, which is almost always what you want. Paged: pass offset=nextOffset to continue, and nextOffset is absent when there is no more. This is reachability through the call graph, not runtime coverage — a method listed here is unreached by any test's call chain, including setup done in test-class lifecycle hooks, interface dispatch, base-class overrides, the accessor bodies of properties the chain reads or writes, and the static initialization of types it touches. Steps the walk cannot follow (stored delegates, reflection) can list a method here that tests do exercise; the list is a set of candidates to read, not a verdict.")]
    public string UncoveredMethods(
        [Description("Project name to restrict to (e.g. the production assembly)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description("Where to start, for continuation. Pass the nextOffset from the previous page.")] int offset = 0,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = RequireCoverage(() => new GraphQuery(graph).FindUncoveredMethods(project).ToList());

        var (window, methods) = ToolResponses.Paginate(all, offset, limit, n => ToolResponses.NodeSummary(n));

        return ToolResponses.ToJson(new
        {
            returned = window.Returned,
            totalMatches = window.TotalMatches,
            offset = window.Offset,
            truncated = window.Truncated,
            nextOffset = window.NextOffset,
            methods
        });
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
