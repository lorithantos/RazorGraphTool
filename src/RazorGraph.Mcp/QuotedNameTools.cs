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
    [Description("Where code names a declared symbol with a STRING rather than referencing it — the coupling a compiler cannot see and a rename breaks silently. Covers binding paths, DI keys by string, config and route names, reflection by name, and a project naming another project's vocabulary without referencing it. Every row carries a provenance: 'literal' and 'interpolated' break on rename, 'nameof' does not, 'attributeArgument' breaks the framework binding rather than the build. By default reports only the breakable provenances that cross a project boundary, which is the highest-signal set; widen with includeSafe and sameProject. Requires a graph built with build_solution — a single-project graph cannot see the boundary this is about. MATCHING IS BY NAME: a string matching several declarations reports an edge to each, because which one the author meant is not decidable, so read the rows rather than counting them.")]
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
            caveat = "A string is matched to a declaration by NAME EQUALITY, so a common name reports every declaration that carries it and only one of them is what the author meant. Strings matching no declaration are not in the graph at all, so this is never a complete inventory of a codebase's strings — it is the subset that names something. A 'nameof' row is not a defect; it is the same coupling in the form that survives a rename."
        });
    }
}
