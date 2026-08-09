namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;

/// <summary>
/// The exception-escape report: throwing operations that can reach an
/// application entry point with nothing catching them. Carries its blind spots
/// as data — the caveats list is part of every response.
/// </summary>
[McpServerToolType]
public sealed class ExceptionEscapeTools(GraphStore store)
{
    private static readonly string[] EntryPointKinds =
    {
        "main", "pageHandler", "controllerAction", "eventHandler", "asyncVoid",
        "frameworkOverride", "frameworkInterface", "middleware", "callback"
    };

    private static readonly string[] EscapeCaveats =
    {
        "Throws inside BCL/out-of-solution code are invisible; a chain through framework code is not analyzed.",
        "Calls resolve to their statically bound target; overrides reached by virtual dispatch are not widened.",
        "Delegate registrations are followed one hop: a method group or lambda handed to out-of-solution code marks its target (or the lambda's containing method) as a 'callback' entry point. A delegate stored and forwarded further is not tracked, and local functions are not followed.",
        "A lambda's throws are attributed to its containing method as conditional — the container is the only node the lambda has.",
        "catch clauses with 'when' filters count as conditional handling (conditional=true), never as handling.",
        "Interface dispatch widens to in-solution implementations (any implementation's throws reach the caller); class virtual overrides are not widened.",
        "Boundary interception is catch-set matching: pipeline order and Map branches are not modeled, and an intercepted escape is still reported — disposition says shaped, not safe. Status-code results (404, ProblemDetails returns) are ordinary returns, not exceptions, and never appear here.",
        "Top-level-statement Main is not modeled as an entry point; minimal-API lambda endpoints are not entry points."
    };

    [McpServerTool(Name = "exception_escapes")]
    [Description("Throwing operations that can reach an application entry point (Main, page handler, controller action, event handler, async-void method, framework override, framework-interface implementation, or a callback registered with out-of-solution code) without passing through a catch that handles them — the 'what can crash this process' report, shallowest chain first. Precomputed at build time over static call chains. Blind spots come back as data in 'caveats': out-of-solution (BCL) throwers are invisible, virtual dispatch is not widened, delegate registrations are followed one hop only, and a catch filter counts as conditional handling, not handling.")]
    public string ExceptionEscapes(
        [Description("Restrict to one entry-point kind: main, pageHandler, controllerAction, eventHandler, asyncVoid, frameworkOverride, frameworkInterface, middleware, callback")] string? entryPointKind = null,
        [Description("Case-insensitive substring filter on the escaping exception type")] string? exceptionType = null,
        [Description("Restrict to entry points from this project")] string? project = null,
        [Description("Only escapes reaching this entry-point method node id (m:...)")] string? entryPointId = null,
        [Description("Max escapes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        if (entryPointKind != null && !EntryPointKinds.Contains(entryPointKind, StringComparer.Ordinal))
            throw new McpException(
                $"Unknown entryPointKind '{entryPointKind}'. Valid kinds: {string.Join(", ", EntryPointKinds)}.");

        var graph = store.Require(graphId).Graph;
        if (entryPointId != null) ToolArguments.RequireNode(graph, entryPointId);

        // A graph saved before this analysis existed has neither stamps nor
        // edges; silence would read as "nothing escapes", which is a claim the
        // graph cannot back.
        if (!graph.Edges.Any(e => e.Type == EdgeType.Escapes)
            && !graph.NodesOfType(NodeType.Method).Any(n => n.GetProperty<string>("entryPointKind") != null))
            throw new McpException(
                "This graph predates exception-escape analysis. Rebuild it with build_graph or build_solution.");

        var all = new GraphQuery(graph)
            .FindEscapingExceptions(entryPointKind, exceptionType, project, entryPointId)
            .ToList();
        var page = all.Take(Math.Max(1, limit))
            .Select(e =>
            {
                var interceptedBy = e.Edge.GetProperty<List<string>>("interceptedBy");
                var interceptedConditionally = e.Edge.GetProperty<List<string>>("interceptedConditionallyBy");

                return new
                {
                    exceptionType = e.Edge.GetProperty<string>("exceptionType"),
                    conditional = e.Edge.GetProperty<bool>("conditional"),
                    depth = e.Edge.GetProperty<int>("depth"),
                    // A shaped response by design (ValidationException -> 400)
                    // reads differently from a raw 500; both stay reported.
                    disposition = interceptedBy is { Count: > 0 } ? "intercepted"
                        : interceptedConditionally is { Count: > 0 } ? "conditionallyIntercepted"
                        : null,
                    interceptedBy = ResolveBoundaries(graph, interceptedBy, interceptedConditionally),
                    entryPoint = new
                    {
                        node = ToolResponses.NodeSummary(e.EntryPoint),
                        kind = e.EntryPoint.GetProperty<string>("entryPointKind")
                    },
                    thrownBy = ToolResponses.NodeSummary(e.Thrower),
                    path = (e.Edge.GetProperty<List<string>>("path") ?? new List<string>())
                        .Select(id => new { id, name = graph.GetNode(id)?.Name })
                        .ToList()
                };
            })
            .ToList();

        return ToolResponses.ToJson(new
        {
            returned = page.Count,
            totalMatches = all.Count,
            truncated = all.Count > page.Count,
            escapes = page,
            caveats = EscapeCaveats
        });
    }

    private static object? ResolveBoundaries(
        CodeGraph graph, List<string>? firm, List<string>? conditional)
    {
        if (firm is not { Count: > 0 } && conditional is not { Count: > 0 }) return null;

        return (firm ?? new List<string>())
            .Select(id => new { id, name = graph.GetNode(id)?.Name, conditional = false })
            .Concat((conditional ?? new List<string>())
                .Select(id => new { id, name = graph.GetNode(id)?.Name, conditional = true }))
            .ToList();
    }
}
