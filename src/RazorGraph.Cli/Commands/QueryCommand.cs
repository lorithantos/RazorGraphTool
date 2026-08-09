namespace RazorGraph.Cli;

using System.CommandLine;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;

/// <summary>
/// The query command and its modes: node report, type listing, and the
/// whole-graph reports (mismatches, escapes, uncovered, deep nesting).
/// </summary>
internal static class QueryCommand
{
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
                ExceptionFilter = parseResult.GetValue(exceptionOpt)
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
            if (options.Mismatches) return RunMismatches(query);
            if (options.Escapes) return RunEscapes(query, options.EntryKind, options.ExceptionFilter, options.Project);
            if (options.Uncovered) return RunUncovered(query, options.Project);
            if (options.Deep > 0) return RunDeepListing(query, options.Deep, options.Project);
            if (options.Id != null) return RunNodeReport(query, options.Id, options);
            if (options.Type != null && Enum.TryParse<NodeType>(options.Type, true, out var type))
                return RunTypeListing(query, type, options.Name, options.Project);
        }
        catch (InvalidOperationException ex)
        {
            // The query layer refuses questions the graph cannot answer (e.g.
            // coverage against a test-less graph). The refusal is the report.
            Console.Error.WriteLine(ex.Message);
            return 1;
        }

        Console.WriteLine("Graph loaded. Use --id, --type, or --mismatches to query.");
        Console.WriteLine($"Total: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        return 0;
    }

    private static int RunMismatches(GraphQuery query)
    {
        var found = 0;
        foreach (var (server, js, edge) in query.FindServerToJsMismatches())
        {
            Console.WriteLine($"  [{server.Type}] {server.Name} --{edge.Type}--> [{js.Type}] {js.Name}");
            found++;
        }
        Console.WriteLine(found == 0
            ? "No server-to-JS mismatches found."
            : $"{found} server-to-JS mismatch(es).");
        return 0;
    }

    private static int RunEscapes(GraphQuery query, string? entryKind, string? exceptionFilter, string? project)
    {
        var escapes = query.FindEscapingExceptions(entryKind, exceptionFilter, project).ToList();
        Console.WriteLine($"{escapes.Count} exception escape(s)"
            + (project == null ? " (all projects)" : $" into {project}") + ":");

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
        Console.WriteLine("Static reachability over in-solution code only: BCL throwers are invisible,");
        Console.WriteLine("interface dispatch widens to in-solution implementations (class virtual");
        Console.WriteLine("overrides do not), delegate registrations are followed one hop, boundary");
        Console.WriteLine("interception is catch-set matching (pipeline order is not modeled), and");
        Console.WriteLine("top-level-statement Main and minimal-API lambdas are not entry points.");
        return 0;
    }

    private static int RunUncovered(GraphQuery query, string? project)
    {
        var uncovered = query.FindUncoveredMethods(project).ToList();
        Console.WriteLine($"{uncovered.Count} method(s) no test reaches"
            + (project == null ? " (all projects)" : $" in {project}") + ":");
        foreach (var m in uncovered.Take(200))
            Console.WriteLine($"  [{m.GetProperty<string>("project")}] {m.Name}  ({m.FilePath}:{m.LineStart})");
        if (uncovered.Count > 200) Console.WriteLine($"  ... {uncovered.Count - 200} more not shown");
        return 0;
    }

    private static int RunDeepListing(GraphQuery query, int minDepth, string? project)
    {
        var deep = query.FindDeepMethods(minDepth, project).ToList();
        Console.WriteLine($"{deep.Count} method(s) with body nesting depth >= {minDepth}"
            + (project == null ? " (all projects)" : $" in {project}") + ":");
        foreach (var m in deep)
            Console.WriteLine($"  depth {m.GetProperty<int>("bodyDepth")}: [{m.GetProperty<string>("project")}] "
                + $"{m.GetProperty<string>("declaringType")}.{m.Name}  ({m.FilePath}:{m.LineStart})");
        return 0;
    }

    private static int RunNodeReport(GraphQuery query, string nodeId, QueryOptions options)
    {
        var node = query.GetNode(nodeId);
        if (node == null)
        {
            Console.WriteLine($"Node not found: {nodeId}");
            return 1;
        }

        GraphReports.PrintNode(node);

        if (options.Neighbors)
        {
            Console.WriteLine("\n--- Outgoing ---");
            foreach (var (edge, target) in query.GetNeighbors(nodeId))
                Console.WriteLine($"  {edge.Type} -> [{target.Type}] {target.Name}");
        }

        if (options.RenderTree && node.Type == NodeType.RazorPage)
        {
            Console.WriteLine("\n--- Render Tree ---");
            foreach (var (n, e) in query.GetRenderTree(nodeId))
                Console.WriteLine($"  {e.Type} -> [{n.Type}] {n.Name}");
        }

        if (options.Context)
            PrintPageContext(query, nodeId);

        if (options.Trace && PrintDataFlow(query, nodeId, options.Depth, options.Direction) != 0)
            return 1;

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
            Console.WriteLine($"    [{svc.Type}] {svc.Name}");
    }

    private static int PrintDataFlow(GraphQuery query, string nodeId, int depth, string directionText)
    {
        if (!Enum.TryParse<TraversalDirection>(directionText, true, out var direction))
        {
            Console.Error.WriteLine($"Unknown --direction '{directionText}'. Valid: outgoing, incoming, both.");
            return 1;
        }

        Console.WriteLine($"\n--- Data Flow ({direction}) ---");
        foreach (var (n, e, d) in query.TraceDataFlow(nodeId, depth, direction))
            Console.WriteLine($"  {new string(' ', d * 2)}{e.Type} -> [{n.Type}] {n.Name}");
        return 0;
    }

    private static int RunTypeListing(GraphQuery query, NodeType type, string? name, string? project)
    {
        var nodes = query.FindNodes(type, name)
            .Where(n => project == null ||
                        string.Equals(n.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
            .ToList();
        Console.WriteLine($"Found {nodes.Count} nodes of type {type}:");
        foreach (var n in nodes)
            Console.WriteLine($"  [{n.Id}] {n.Name} ({n.FilePath})");
        return 0;
    }
}
