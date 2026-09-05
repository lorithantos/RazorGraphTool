namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;

/// <summary>
/// Where code names a declared symbol with a string instead of referencing it.
/// </summary>
[McpServerToolType]
internal sealed class QuotedNameTools(GraphStore store)
{
    [McpServerTool(Name = "quoted_symbols")]
    [Description("Where code names a declared symbol with a STRING rather than referencing it — the coupling a compiler cannot see and a rename breaks silently. Covers binding paths, DI keys by string, config and route names, reflection by name, and a project naming another project's vocabulary without referencing it. Every row carries a provenance: 'literal' and 'interpolated' break on rename, 'nameof' does not, 'attributeArgument' breaks the framework binding rather than the build. By default reports only the breakable provenances that cross a project boundary, which is the highest-signal set; widen with includeSafe and sameProject. Requires a graph built with build_solution — a single-project graph cannot see the boundary this is about. WORK IN PROGRESS and heuristic in both directions — rows are candidates to read, not a verdict. Matching is by name and only when that name is UNIQUE in the solution, so it under-reports: a string naming something called Dispose or Name is dropped entirely, because the author meant one of several and the graph cannot say which. It also over-reports, because a string that happens to equal an unrelated declaration's name matches it. Read the row and decide; the value and line are there so you can.")]
    public string QuotedSymbols(
        [Description("Restrict to strings produced by code in this project")] string? project = null,
        [Description("Include nameof, which survives a rename. Off by default: it is the safe form of the same coupling.")] bool includeSafe = false,
        [Description("Include strings naming a symbol in their own project. Off by default — cross-boundary naming is the question this answers.")] bool sameProject = false,
        [Description("Max rows to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;

        var all = new GraphQuery(graph)
            .FindQuotedSymbols(project, includeSafe, sameProject)
            .ToList();

        var page = all.Take(Math.Max(1, limit)).Select(q => new
        {
            value = q.Value,
            provenance = q.Provenance,
            line = q.Line,
            from = ToolResponses.NodeSummary(q.From),
            to = ToolResponses.NodeSummary(q.To)
        }).ToList();

        return ToolResponses.ToJson(new
        {
            returned = page.Count,
            totalMatches = all.Count,
            truncated = all.Count > page.Count,
            quotes = page,
            caveat = "Name equality is a heuristic and it errs both ways. It UNDER-reports: a string matching no declaration is not an edge, and neither is one matching a name several declarations share, so an empty result means no unambiguous naming was found and never that a codebase has none. It OVER-reports: a string that merely happens to equal a declaration's name is indistinguishable from one that means it, and on this repo's own graph several rows are that coincidence. Read the value and line before believing a row. A 'nameof' row is not a defect at all; it is the same coupling in the form that survives a rename."
        });
    }
}
