namespace RazorGraph.Cli;

using System.CommandLine;
using System.Text.Json;
using System.Text.Json.Serialization;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;

/// <summary>
/// The query command and its modes: node report, type listing, and the
/// whole-graph reports (mismatches, escapes, uncovered, deep nesting).
///
/// Every mode computes its data first and renders second, so text and JSON are
/// two views of one result rather than two code paths that can drift. The JSON
/// envelope exists because the likelier consumer of this tool is an agent, not a
/// person: a human reads the text; everything else was scraping it.
/// </summary>
internal static class QueryCommand
{
    /// <summary>
    /// Envelope version for --json output. Bump on any change to the shape of
    /// existing fields; additive fields do not bump it. Carried in every
    /// envelope so a consumer can notice drift instead of mis-parsing quietly —
    /// the same convention Invoke-DotnetCheck stamps on its output.
    /// </summary>
    private const int JsonContract = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static Command Query()
    {
        var graphArg = new Argument<FileInfo>("graph") { Description = "Path to a built graph JSON file" };
        var idOpt = new Option<string?>("--id") { Description = "Look up a node by its stable id" };
        var typeOpt = new Option<string?>("--type") { Description = "Find nodes by NodeType (e.g. RazorPage)" };
        var nameOpt = new Option<string?>("--name") { Description = "Filter found nodes by name substring" };
        var neighborsOpt = new Option<bool>("--neighbors") { Description = "With --id: list outgoing edges" };
        var renderTreeOpt = new Option<bool>("--render-tree") { Description = "With --id of a page: show layout/partial render tree" };
        var contextOpt = new Option<bool>("--context") { Description = "With --id of a page: show PageModel, ViewModel, and injected services" };
        var traceOpt = new Option<bool>("--trace") { Description = "With --id: trace data flow edges" };
        var depthOpt = new Option<int>("--depth") { Description = "Max traversal depth for --trace", DefaultValueFactory = _ => 3 };
        var directionOpt = new Option<string>("--direction")
        {
            Description = "Traversal direction for --trace: outgoing (default), incoming, or both",
            DefaultValueFactory = _ => "outgoing"
        };
        var projectOpt = new Option<string?>("--project") { Description = "Restrict --type or --uncovered to one project" };
        var coveringOpt = new Option<bool>("--covering-tests") { Description = "With --id of a method: which tests exercise it" };
        var coveredOpt = new Option<bool>("--covered-methods") { Description = "With --id of a test: which methods it exercises" };
        var uncoveredOpt = new Option<bool>("--uncovered") { Description = "List methods no test reaches (use with --project)" };
        var deepOpt = new Option<int>("--deep") { Description = "List methods whose body nests control flow at least this deep (use with --project)" };
        var mismatchesOpt = new Option<bool>("--mismatches") { Description = "Report server-prepared data consumed by client JS" };
        var escapesOpt = new Option<bool>("--escapes") { Description = "Report exceptions that can reach an entry point uncaught (use with --project, --entry-kind, --exception)" };
        var entryKindOpt = new Option<string?>("--entry-kind") { Description = "With --escapes: restrict to one entry-point kind (main, pageHandler, controllerAction, eventHandler, asyncVoid, frameworkOverride, frameworkInterface, callback)" };
        var exceptionOpt = new Option<string?>("--exception") { Description = "With --escapes: case-insensitive substring filter on the escaping exception type" };
        var jsonOpt = new Option<bool>("--json") { Description = "Emit a contract-numbered JSON envelope instead of text. For agents and scripts; the shape is versioned by the 'contract' field." };

        var cmd = new Command("query", "Run a query against a built graph");
        cmd.Add(graphArg);
        cmd.Add(idOpt);
        cmd.Add(typeOpt);
        cmd.Add(nameOpt);
        cmd.Add(neighborsOpt);
        cmd.Add(renderTreeOpt);
        cmd.Add(contextOpt);
        cmd.Add(traceOpt);
        cmd.Add(depthOpt);
        cmd.Add(directionOpt);
        cmd.Add(projectOpt);
        cmd.Add(coveringOpt);
        cmd.Add(coveredOpt);
        cmd.Add(uncoveredOpt);
        cmd.Add(deepOpt);
        cmd.Add(mismatchesOpt);
        cmd.Add(escapesOpt);
        cmd.Add(entryKindOpt);
        cmd.Add(exceptionOpt);
        cmd.Add(jsonOpt);

        cmd.SetAction((parseResult, ct) => RunQueryAsync(
            parseResult.GetValue(graphArg)!,
            new QueryOptions
            {
                Id = parseResult.GetValue(idOpt),
                Type = parseResult.GetValue(typeOpt),
                Name = parseResult.GetValue(nameOpt),
                Project = parseResult.GetValue(projectOpt),
                Neighbors = parseResult.GetValue(neighborsOpt),
                RenderTree = parseResult.GetValue(renderTreeOpt),
                Context = parseResult.GetValue(contextOpt),
                Trace = parseResult.GetValue(traceOpt),
                Depth = parseResult.GetValue(depthOpt),
                Direction = parseResult.GetValue(directionOpt)!,
                CoveringTests = parseResult.GetValue(coveringOpt),
                CoveredMethods = parseResult.GetValue(coveredOpt),
                Uncovered = parseResult.GetValue(uncoveredOpt),
                Deep = parseResult.GetValue(deepOpt),
                Mismatches = parseResult.GetValue(mismatchesOpt),
                Escapes = parseResult.GetValue(escapesOpt),
                EntryKind = parseResult.GetValue(entryKindOpt),
                ExceptionFilter = parseResult.GetValue(exceptionOpt),
                Json = parseResult.GetValue(jsonOpt)
            },
            ct));

        return cmd;
    }

    /// <summary>Everything the query command parsed, so the modes below take one argument, not fifteen.</summary>
    private sealed record QueryOptions
    {
        public string? Id { get; init; }
        public string? Type { get; init; }
        public string? Name { get; init; }
        public string? Project { get; init; }
        public bool Neighbors { get; init; }
        public bool RenderTree { get; init; }
        public bool Context { get; init; }
        public bool Trace { get; init; }
        public int Depth { get; init; }
        public required string Direction { get; init; }
        public bool CoveringTests { get; init; }
        public bool CoveredMethods { get; init; }
        public bool Uncovered { get; init; }
        public int Deep { get; init; }
        public bool Mismatches { get; init; }
        public bool Escapes { get; init; }
        public string? EntryKind { get; init; }
        public string? ExceptionFilter { get; init; }
        public bool Json { get; init; }
    }

    private static async Task<int> RunQueryAsync(FileInfo graphFile, QueryOptions options, CancellationToken ct)
    {
        var graph = await GraphFiles.LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;
        var query = new GraphQuery(graph);

        try
        {
            // Mode precedence, most specific first. This ordering is the command's
            // contract; it used to be implicit in the order of if-blocks inside one
            // long lambda.
            if (options.Mismatches) return RunMismatches(query, options);
            if (options.Escapes) return RunEscapes(query, options);
            if (options.Uncovered) return RunUncovered(query, options);
            if (options.Deep > 0) return RunDeepListing(query, options);
            if (options.Id != null) return RunNodeReport(query, graph, options.Id, options);
            // Any kind the graph actually holds, foreign ones included. An
            // unparseable --type used to fall through to the generic "Graph
            // loaded" banner and exit 0, so a typo reported success and no
            // results -- the caller could not tell a bad argument from an empty
            // graph.
            if (options.Type != null) return RunTypeListing(query, graph, options);
        }
        catch (InvalidOperationException ex)
        {
            // The query layer refuses questions the graph cannot answer (e.g.
            // coverage against a test-less graph). The refusal is the report --
            // and in JSON mode it must still BE the report, structured, because
            // an agent that only sees exit 1 has been told nothing.
            if (options.Json)
            {
                Emit(new { contract = JsonContract, mode = "error", error = ex.Message });
                return 1;
            }
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        if (options.Json)
        {
            Emit(new { contract = JsonContract, mode = "summary", nodes = graph.Nodes.Count, edges = graph.Edges.Count });
            return 0;
        }

        Console.WriteLine("Graph loaded. Use --id, --type, or --mismatches to query.");
        Console.WriteLine($"Total: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        return 0;
    }

    private static void Emit(object envelope) =>
        Console.WriteLine(JsonSerializer.Serialize(envelope, JsonOptions));

    /// <summary>
    /// Lean node summary for JSON results: enough to act on, no property bag —
    /// except for what the node reports as BROKEN, which travels with it.
    /// </summary>
    private static object NodeRef(GraphNode n) => new
    {
        id = n.Id,
        type = n.DisplayType,
        name = n.Name,
        file = n.FilePath,
        line = n.LineStart,
        project = n.GetProperty<string>("project"),
        findings = FindingsOn(n)
    };

    /// <summary>
    /// What this node reports as broken, or null when nothing is.
    ///
    /// A listing is otherwise an index — id, name, where it lives — and a finding
    /// recorded only as a node property is invisible to anyone who did not
    /// already know to fetch that node and look. Listing 265 shape names without
    /// saying that three of them are served by nothing is the absence-as-finding
    /// trap in report form.
    ///
    /// One function so a new finding-bearing property has one place to be added,
    /// rather than being remembered at each surface.
    /// </summary>
    private static List<string>? FindingsOn(GraphNode n)
    {
        var findings = new List<string>();

        // Unbound, but only where something requires a template: an alternate or
        // a test fixture records the same flag and is not a failure.
        if (n.GetProperty<bool>("unbound") && !n.GetProperty<bool>("noRequiredProducer"))
        {
            var requiredBy = n.GetProperty<List<string>>("requiredBy");
            findings.Add(requiredBy is { Count: > 0 }
                ? $"nothing serves this name; required by {string.Join(", ", requiredBy)}"
                : "nothing serves this name");
        }

        foreach (var missing in n.GetProperty<List<string>>("missingViews") ?? [])
            findings.Add($"names a view no template serves at {missing}");

        foreach (var unresolved in n.GetProperty<List<string>>("unresolvedViews") ?? [])
            findings.Add($"view name could not be determined at {unresolved}");

        return findings.Count > 0 ? findings : null;
    }

    private static int RunMismatches(GraphQuery query, QueryOptions options)
    {
        var found = query.FindServerToJsMismatches().ToList();

        if (options.Json)
        {
            Emit(new
            {
                contract = JsonContract,
                mode = "mismatches",
                returned = found.Count,
                results = found.Select(m => new { server = NodeRef(m.ServerNode), js = NodeRef(m.JsNode), edgeType = m.Edge.DisplayType })
            });
            return 0;
        }

        foreach (var (server, js, edge) in found)
            Console.WriteLine($"  [{server.DisplayType}] {server.Name} --{edge.DisplayType}--> [{js.DisplayType}] {js.Name}");
        Console.WriteLine(found.Count == 0
            ? "No server-to-JS mismatches found."
            : $"{found.Count} server-to-JS mismatch(es).");
        return 0;
    }

    private static int RunEscapes(GraphQuery query, QueryOptions options)
    {
        const string caveat =
            "Static reachability over in-solution code only: BCL throwers are invisible, "
            + "interface dispatch widens to in-solution implementations (class virtual overrides do not), "
            + "delegate registrations are followed one hop, boundary interception is catch-set matching "
            + "(pipeline order is not modeled), and top-level-statement Main and minimal-API lambdas are not entry points.";

        var escapes = query.FindEscapingExceptions(options.EntryKind, options.ExceptionFilter, options.Project).ToList();

        if (options.Json)
        {
            // The caveat rides the envelope. An honest result names its own
            // limits; an agent that never sees them will treat reachability as
            // proof.
            Emit(new
            {
                contract = JsonContract,
                mode = "escapes",
                project = options.Project,
                returned = escapes.Count,
                results = escapes.Select(e => new
                {
                    exceptionType = e.Edge.GetProperty<string>("exceptionType"),
                    thrower = NodeRef(e.Thrower),
                    entryPoint = NodeRef(e.EntryPoint),
                    entryPointKind = e.EntryPoint.GetProperty<string>("entryPointKind"),
                    depth = e.Edge.GetProperty<int>("depth"),
                    conditional = e.Edge.GetProperty<bool>("conditional") ? true : (bool?)null,
                    shapedByBoundary = e.Edge.GetProperty<List<string>>("interceptedBy") is { Count: > 0 } s ? s : null,
                    conditionallyShapedBy = e.Edge.GetProperty<List<string>>("interceptedConditionallyBy") is { Count: > 0 } c ? c : null
                }),
                caveats = new[] { caveat }
            });
            return 0;
        }

        Console.WriteLine($"{escapes.Count} exception escape(s)"
            + (options.Project == null ? " (all projects)" : $" into {options.Project}") + ":");
        foreach (var (thrower, entry, edge) in escapes)
        {
            var conditional = edge.GetProperty<bool>("conditional") ? " (conditional — filtered catch en route)" : "";
            Console.WriteLine(
                $"  {edge.GetProperty<string>("exceptionType")} -> "
                + $"[{entry.GetProperty<string>("entryPointKind")}] {entry.GetProperty<string>("declaringType")}.{entry.Name} "
                + $"({entry.FilePath}:{entry.LineStart}){conditional}");
            Console.WriteLine(
                $"    thrown by {thrower.GetProperty<string>("declaringType")}.{thrower.Name}, "
                + $"depth {edge.GetProperty<int>("depth")}");

            if (edge.GetProperty<List<string>>("interceptedBy") is { Count: > 0 } shapedBy)
                Console.WriteLine($"    shaped by boundary: {string.Join(", ", shapedBy)} — a designed response, not a raw failure");
            else if (edge.GetProperty<List<string>>("interceptedConditionallyBy") is { Count: > 0 } maybeShapedBy)
                Console.WriteLine($"    conditionally shaped by: {string.Join(", ", maybeShapedBy)} — the boundary may decline at runtime");
        }
        Console.WriteLine();
        Console.WriteLine(caveat);
        return 0;
    }

    private static int RunUncovered(GraphQuery query, QueryOptions options)
    {
        var uncovered = query.FindUncoveredMethods(options.Project).ToList();

        if (options.Json)
        {
            // No cap in JSON: the 200-row limit exists to keep a terminal
            // readable, and an agent asked for the data, not a page of it. The
            // envelope still reports its own size so a consumer can notice bulk.
            Emit(new
            {
                contract = JsonContract,
                mode = "uncovered",
                project = options.Project,
                returned = uncovered.Count,
                results = uncovered.Select(NodeRef)
            });
            return 0;
        }

        Console.WriteLine($"{uncovered.Count} method(s) no test reaches"
            + (options.Project == null ? " (all projects)" : $" in {options.Project}") + ":");
        foreach (var m in uncovered.Take(200))
            Console.WriteLine($"  [{m.GetProperty<string>("project")}] {m.Name}  ({m.FilePath}:{m.LineStart})");
        if (uncovered.Count > 200) Console.WriteLine($"  ... {uncovered.Count - 200} more not shown");
        return 0;
    }

    private static int RunDeepListing(GraphQuery query, QueryOptions options)
    {
        var deep = query.FindDeepMethods(options.Deep, options.Project).ToList();

        if (options.Json)
        {
            Emit(new
            {
                contract = JsonContract,
                mode = "deep",
                minDepth = options.Deep,
                project = options.Project,
                returned = deep.Count,
                results = deep.Select(m => new
                {
                    depth = m.GetProperty<int>("bodyDepth"),
                    declaringType = m.GetProperty<string>("declaringType"),
                    node = NodeRef(m)
                })
            });
            return 0;
        }

        Console.WriteLine($"{deep.Count} method(s) with body nesting depth >= {options.Deep}"
            + (options.Project == null ? " (all projects)" : $" in {options.Project}") + ":");
        foreach (var m in deep)
            Console.WriteLine($"  depth {m.GetProperty<int>("bodyDepth")}: [{m.GetProperty<string>("project")}] "
                + $"{m.GetProperty<string>("declaringType")}.{m.Name}  ({m.FilePath}:{m.LineStart})");
        return 0;
    }

    // Takes the graph as well as the query, the same way RunTypeListing does:
    // freshness is a property of the graph, not of a traversal over it.
    private static int RunNodeReport(
        GraphQuery query, CodeGraph graph, string nodeId, QueryOptions options)
    {
        var node = query.GetNode(nodeId);
        if (node == null)
        {
            if (options.Json)
            {
                Emit(new { contract = JsonContract, mode = "node", error = $"Node not found: {nodeId}" });
                return 1;
            }
            Console.WriteLine($"Node not found: {nodeId}");
            return 1;
        }

        if (!Enum.TryParse<TraversalDirection>(options.Direction, true, out var direction))
        {
            var msg = $"Unknown --direction '{options.Direction}'. Valid: outgoing, incoming, both.";
            if (options.Json) { Emit(new { contract = JsonContract, mode = "node", error = msg }); return 1; }
            Console.Error.WriteLine(msg);
            return 1;
        }

        if (options.Json)
        {
            var context = options.Context ? query.GetPageContext(nodeId) : null;
            Emit(new
            {
                contract = JsonContract,
                mode = "node",
                node = NodeRef(node),
                properties = node.Properties.Count > 0 ? node.Properties : null,
                labels = node.Labels.Count > 0 ? node.Labels : null,
                neighbors = options.Neighbors
                    ? query.GetNeighbors(nodeId).Select(x => new { edgeType = x.Edge.DisplayType, target = NodeRef(x.Target) })
                    : null,
                renderTree = options.RenderTree && node.Type == NodeType.RazorPage
                    ? query.GetRenderTree(nodeId).Select(x => new { edgeType = x.Edge.DisplayType, node = NodeRef(x.Node) })
                    : null,
                pageContext = context is null ? null : new
                {
                    pageModel = context.PageModel is null ? null : NodeRef(context.PageModel),
                    viewModel = context.ViewModel is null ? null : NodeRef(context.ViewModel),
                    injectedServices = context.InjectedServices.Select(NodeRef)
                },
                dataFlow = options.Trace
                    ? query.TraceDataFlow(nodeId, options.Depth, direction)
                        .Select(x => new { depth = x.Depth, edgeType = x.Edge.DisplayType, node = NodeRef(x.Node) })
                    : null,
                coveringTests = options.CoveringTests
                    ? query.GetCoveringTests(nodeId).Select(x => new { depth = x.Depth, test = NodeRef(x.Test) })
                    : null,
                coveredMethods = options.CoveredMethods
                    ? query.GetCoveredMethods(nodeId).Select(x => new { depth = x.Depth, method = NodeRef(x.Method) })
                    : null
            });
            return 0;
        }

        GraphReports.PrintNode(node, new GraphFreshness(graph));

        if (options.Neighbors)
        {
            Console.WriteLine("\n--- Outgoing ---");
            foreach (var (edge, target) in query.GetNeighbors(nodeId))
                Console.WriteLine($"  {edge.DisplayType} -> [{target.DisplayType}] {target.Name}");
        }

        if (options.RenderTree && node.Type == NodeType.RazorPage)
        {
            Console.WriteLine("\n--- Render Tree ---");
            foreach (var (n, e) in query.GetRenderTree(nodeId))
                Console.WriteLine($"  {e.DisplayType} -> [{n.DisplayType}] {n.Name}");
        }

        if (options.Context)
            PrintPageContext(query, nodeId);

        if (options.Trace)
        {
            Console.WriteLine($"\n--- Data Flow ({direction}) ---");
            foreach (var (n, e, d) in query.TraceDataFlow(nodeId, options.Depth, direction))
                Console.WriteLine($"  {new string(' ', d * 2)}{e.DisplayType} -> [{n.DisplayType}] {n.Name}");
        }

        if (options.CoveringTests)
        {
            Console.WriteLine("\n--- Covering Tests ---");
            var tests = query.GetCoveringTests(nodeId).ToList();
            if (tests.Count == 0) Console.WriteLine("  (none — no test's call chain reaches this method)");
            foreach (var (test, depth) in tests)
                Console.WriteLine($"  depth {depth}: {test.Name}  ({test.FilePath}:{test.LineStart})");
        }

        if (options.CoveredMethods)
        {
            Console.WriteLine("\n--- Covered Methods ---");
            var methods = query.GetCoveredMethods(nodeId).ToList();
            if (methods.Count == 0) Console.WriteLine("  (none)");
            foreach (var (method, depth) in methods)
                Console.WriteLine($"  depth {depth}: [{method.GetProperty<string>("project")}] {method.Name}");
        }

        return 0;
    }

    private static void PrintPageContext(GraphQuery query, string nodeId)
    {
        Console.WriteLine("\n--- Page Context ---");
        var context = query.GetPageContext(nodeId);
        if (context == null)
        {
            Console.WriteLine("  Not a Razor page node; no context available.");
            return;
        }

        Console.WriteLine($"  PageModel: {GraphReports.Describe(context.PageModel)}");
        Console.WriteLine($"  ViewModel: {GraphReports.Describe(context.ViewModel)}");
        Console.WriteLine($"  Injected services ({context.InjectedServices.Count}):");
        foreach (var svc in context.InjectedServices)
            Console.WriteLine($"    [{svc.DisplayType}] {svc.Name}");
    }

    private static int RunTypeListing(GraphQuery query, CodeGraph graph, QueryOptions options)
    {
        var kind = ResolveKind(graph, options.Type!, options.Json);
        if (kind == null) return 1;

        var nodes = query.FindNodes(kind, options.Name)
            .Where(n => options.Project == null ||
                        string.Equals(n.GetProperty<string>("project"), options.Project, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (options.Json)
        {
            Emit(new { contract = JsonContract, mode = "type", type = kind, name = options.Name, project = options.Project, returned = nodes.Count, results = nodes.Select(NodeRef) });
            return 0;
        }

        Console.WriteLine($"Found {nodes.Count} nodes of type {kind}:");
        foreach (var n in nodes)
        {
            Console.WriteLine($"  [{n.Id}] {n.Name} ({n.FilePath})");

            // The same rule the JSON rows follow: a listing that stays silent
            // about the broken ones reads as a clean bill of health.
            foreach (var finding in FindingsOn(n) ?? []) Console.WriteLine($"      ! {finding}");
        }
        return 0;
    }

    /// <summary>
    /// Resolve a requested kind against this build's vocabulary and the foreign
    /// kinds the graph actually holds, or report what it could have been. Null
    /// means the caller was told; do not also print results.
    /// </summary>
    private static string? ResolveKind(CodeGraph graph, string requested, bool json)
    {
        var trimmed = requested.Trim();

        if (Enum.TryParse<NodeType>(trimmed, ignoreCase: true, out var known) && known != NodeType.Unknown)
            return known.ToString();

        var foreign = graph.ForeignNodeKinds
            .FirstOrDefault(k => string.Equals(k, trimmed, StringComparison.OrdinalIgnoreCase));
        if (foreign != null) return foreign;

        var knownTypes = Enum.GetNames<NodeType>().Where(n => n != nameof(NodeType.Unknown)).ToList();
        if (json)
        {
            Emit(new
            {
                contract = JsonContract,
                mode = "type",
                error = $"unknown node type '{requested}'",
                knownTypes,
                foreignKindsInGraph = graph.ForeignNodeKinds
            });
            return null;
        }

        Console.Error.WriteLine($"error: unknown node type '{requested}'.");
        Console.Error.WriteLine($"  known types: {string.Join(", ", knownTypes)}");
        Console.Error.WriteLine(graph.ForeignNodeKinds.Count > 0
            ? $"  foreign kinds in this graph: {string.Join(", ", graph.ForeignNodeKinds)}"
            : "  this graph carries no foreign node kinds.");
        return null;
    }

}
