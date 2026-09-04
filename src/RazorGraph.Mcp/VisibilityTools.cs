namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol.Server;
using RazorGraph.Core.Query;

/// <summary>
/// Tools that answer "who can actually reach this" — declared visibility set
/// against observed consumers.
/// </summary>
[McpServerToolType]
public sealed class VisibilityTools(GraphStore store)
{
    /// <summary>
    /// Carried in every response, not just the tool description: a caller that
    /// pages through results should not have to remember a caveat it read once.
    /// </summary>
    private const string Caveat =
        "WORK IN PROGRESS and deliberately aggressive — these are candidates to verify, not a verdict. "
        + "Reflection, DI registration by string or open generic, and serialization all consume a type with no edge to prove it, "
        + "and a project published as a package is SUPPOSED to have consumers outside this solution. "
        + "Results are also INTERDEPENDENT: a member and the types only it exposes are reported together, "
        + "so apply the set rather than a single line. Let the compiler have the final word.";

    [McpServerTool(Name = "excess_visibility")]
    [Description("WORK IN PROGRESS, deliberately aggressive. Public types and methods that nothing outside their own assembly uses — candidates to make internal, not a verdict. Test projects do not count as consumers by default, since InternalsVisibleTo covers them; pass includeTests to change that. Types pinned public by appearing in an externally-used method's signature are excluded automatically, so results should at least compile — but reflection, DI and serialization consume types with no edge to prove it, so verify before acting. Scope with project: across a whole solution the answer means little.")]
    public string ExcessVisibility(
        [Description("Project name to restrict to. Strongly recommended — a package project is meant to have consumers outside the solution.")] string? project = null,
        [Description("Count test projects as real consumers. Off by default; on, this mostly reports nothing.")] bool includeTests = false,
        [Description("Also report properties and fields. Off by default — they swamp the result without adding a decision, since a record's properties are its shape rather than separately narrowable surface.")] bool includeMembers = false,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = new GraphQuery(graph).FindExcessVisibility(project, includeTests, includeMembers).ToList();
        var page = all.Take(Math.Max(1, limit)).ToList();

        return ToolResponses.ToJson(new
        {
            returned = page.Count,
            totalMatches = all.Count,
            truncated = all.Count > page.Count,
            caveat = Caveat,
            candidates = page.Select(r => new
            {
                node = ToolResponses.NodeSummary(r.Node),
                // Always same-assembly or empty by construction — shown because
                // "used by nobody at all" and "used only from inside" are
                // different findings with different fixes.
                consumedBy = r.ConsumedBy.Count > 0 ? r.ConsumedBy : null
            })
        });
    }
}
