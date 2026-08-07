using System.CommandLine;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Roslyn;

var root = new RootCommand("RazorGraph — queryable code graph of ASP.NET Core Razor apps");
root.Add(CliCommands.Build());
root.Add(CliCommands.BuildSolution());
root.Add(CliCommands.Query());
root.Add(CliCommands.Body());
root.Add(CliCommands.BodyDiff());
root.Add(CliCommands.Research());
return await root.Parse(args).InvokeAsync();

/// <summary>
/// Every command's logic lives here as a named static, not in a SetAction
/// lambda. Anonymous blocks compile under unrecoverable names, cannot be
/// called from tests, and are invisible to the code graph — this tool could
/// not see its own CLI. Each SetAction body below is pure plumbing: read the
/// parsed values, hand them to the method that does the work.
/// </summary>
internal static class CliCommands
{
    public static Command Build()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .csproj or .sln file" };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Output graph JSON file",
            DefaultValueFactory = _ => "graph.json"
        };
        var projectOpt = new Option<string?>("--project")
        {
            Description = "Project name inside the solution (required when path is a .sln)"
        };
        var includeVendorOpt = new Option<bool>("--include-vendor")
        {
            Description = "Also graph vendor/minified client assets (dropped by default); their nodes carry vendor=true"
        };

        var cmd = new Command("build", "Build graph from a project or solution and output JSON");
        cmd.Add(pathArg);
        cmd.Add(outputOpt);
        cmd.Add(projectOpt);
        cmd.Add(includeVendorOpt);

        cmd.SetAction((parseResult, ct) => RunBuildAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(outputOpt)!,
            parseResult.GetValue(projectOpt),
            parseResult.GetValue(includeVendorOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunBuildAsync(
        FileInfo path, string outputPath, string? projectName, bool includeVendor, CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        var isSolution = IsSolutionFile(path);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
        {
            Console.Error.WriteLine("--project <name> is required when building from a solution.");
            return 1;
        }

        RoslynExtractor.EnsureMsBuildRegistered();

        Console.WriteLine($"Building graph from {path.FullName}...");

        await using var builder = new GraphBuilder { IncludeVendorAssets = includeVendor };
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(path.FullName, projectName!, ct)
            : await builder.BuildFromProjectAsync(path.FullName, ct);

        await WriteGraphAsync(graph, outputPath, ct);

        PrintSummary(graph);
        return 0;
    }

    public static Command BuildSolution()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .sln or .slnx file" };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Output graph JSON file",
            DefaultValueFactory = _ => "solution-graph.json"
        };

        var includeVendorOpt = new Option<bool>("--include-vendor")
        {
            Description = "Also graph vendor/minified client assets (dropped by default); their nodes carry vendor=true"
        };

        var cmd = new Command("build-solution",
            "Build ONE graph spanning every project in a solution, with edges that cross project boundaries");
        cmd.Add(pathArg);
        cmd.Add(outputOpt);
        cmd.Add(includeVendorOpt);

        cmd.SetAction((parseResult, ct) => RunBuildSolutionAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(outputOpt)!,
            parseResult.GetValue(includeVendorOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunBuildSolutionAsync(
        FileInfo path, string outputPath, bool includeVendor, CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        if (!IsSolutionFile(path))
        {
            Console.Error.WriteLine($"Not a solution file: {path.FullName}. Use 'build' for a .csproj.");
            return 1;
        }

        RoslynExtractor.EnsureMsBuildRegistered();
        Console.WriteLine($"Building solution graph from {path.FullName}...");

        await using var builder = new GraphBuilder { IncludeVendorAssets = includeVendor };
        var graph = await builder.BuildFromSolutionAllAsync(path.FullName, ct);

        await WriteGraphAsync(graph, outputPath, ct);

        var projects = graph.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();
        if (projects.Count > 0)
            Console.WriteLine($"Projects: {string.Join(", ", projects)}");

        PrintSummary(graph);
        PrintEdgeSummary(graph);
        return 0;
    }

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
        var entryKindOpt = new Option<string?>("--entry-kind") { Description = "With --escapes: restrict to one entry-point kind (main, pageHandler, controllerAction, eventHandler, asyncVoid, frameworkOverride)" };
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
        var graph = await LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;
        var query = new GraphQuery(graph);

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
        }

        Console.WriteLine();
        Console.WriteLine("Static reachability over in-solution code only: BCL throwers are invisible,");
        Console.WriteLine("virtual dispatch is not widened, lambdas/local functions are not followed,");
        Console.WriteLine("and top-level-statement Main is not an entry point.");
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

        PrintNode(node);

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

        Console.WriteLine($"  PageModel: {Describe(context.PageModel)}");
        Console.WriteLine($"  ViewModel: {Describe(context.ViewModel)}");
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

    public static Command Body()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .csproj, .sln, or .slnx file" };
        var methodOpt = new Option<string>("--method")
        {
            Description = "Method node id (m:...) whose body to graph",
            Required = true
        };
        var projectOpt = new Option<string?>("--project")
        {
            Description = "Project name inside the solution; narrows the compile when path is a solution"
        };

        var cmd = new Command("body",
            "Emit one method's internal graph as JSON: control-flow blocks, regions, and call sites with guard depths. Compiles the project.");
        cmd.Add(pathArg);
        cmd.Add(methodOpt);
        cmd.Add(projectOpt);

        cmd.SetAction((parseResult, ct) => RunBodyAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(methodOpt)!,
            parseResult.GetValue(projectOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunBodyAsync(
        FileInfo path, string methodId, string? projectName, CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        if (IsSolutionFile(path))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(path.FullName, ct);
            else
                await roslyn.LoadSolutionAsync(path.FullName, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(path.FullName, ct);
        }

        var body = roslyn.GetMethodBodyGraph(methodId);
        if (body == null)
        {
            Console.Error.WriteLine($"No body graph for '{methodId}': id not found in the compilation, or the method has no body.");
            return 1;
        }

        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(body, BodyJson));
        return 0;
    }

    private static readonly System.Text.Json.JsonSerializerOptions BodyJson = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public static Command BodyDiff()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .csproj, .sln, or .slnx file" };
        var methodOpt = new Option<string>("--method")
        {
            Description = "Method node id (m:...) — the right side of the comparison",
            Required = true
        };
        var againstOpt = new Option<string?>("--against")
        {
            Description = "Another method id in the same compilation to compare against (left side)"
        };
        var baselineOpt = new Option<FileInfo?>("--baseline")
        {
            Description = "A body-graph JSON file saved earlier by the body command (left side)"
        };
        var projectOpt = new Option<string?>("--project")
        {
            Description = "Project name inside the solution; narrows the compile when path is a solution"
        };

        var cmd = new Command("body-diff",
            "Prove two method bodies flow-equivalent, or say precisely why not. Exit 0 equivalent, 1 different, 2 error.");
        cmd.Add(pathArg);
        cmd.Add(methodOpt);
        cmd.Add(againstOpt);
        cmd.Add(baselineOpt);
        cmd.Add(projectOpt);

        cmd.SetAction((parseResult, ct) => RunBodyDiffAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(methodOpt)!,
            parseResult.GetValue(againstOpt),
            parseResult.GetValue(baselineOpt),
            parseResult.GetValue(projectOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunBodyDiffAsync(
        FileInfo path, string methodId, string? againstId, FileInfo? baseline, string? projectName, CancellationToken ct)
    {
        if ((againstId == null) == (baseline == null))
        {
            Console.Error.WriteLine("Exactly one of --against or --baseline is required.");
            return 2;
        }
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 2;
        }

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        if (IsSolutionFile(path))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(path.FullName, ct);
            else
                await roslyn.LoadSolutionAsync(path.FullName, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(path.FullName, ct);
        }

        var right = roslyn.GetMethodBodyGraph(methodId);
        if (right == null)
        {
            Console.Error.WriteLine($"No body graph for '{methodId}'.");
            return 2;
        }

        BodyGraph? left;
        if (againstId != null)
        {
            left = roslyn.GetMethodBodyGraph(againstId);
            if (left == null)
            {
                Console.Error.WriteLine($"No body graph for '{againstId}'.");
                return 2;
            }
        }
        else
        {
            if (!baseline!.Exists)
            {
                Console.Error.WriteLine($"Baseline file not found: {baseline.FullName}");
                return 2;
            }
            left = System.Text.Json.JsonSerializer.Deserialize<BodyGraph>(
                await File.ReadAllTextAsync(baseline.FullName, ct), BodyJson);
            if (left == null)
            {
                Console.Error.WriteLine($"Baseline file is not a body graph: {baseline.FullName}");
                return 2;
            }
        }

        var diff = BodyGraphComparer.Compare(left, right);
        Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(diff, BodyJson));
        return diff.Equivalent ? 0 : 1;
    }

    public static Command Research()
    {
        var graphArg = new Argument<FileInfo>("graph") { Description = "Path to a built graph JSON file" };
        var focusOpt = new Option<string[]>("--focus")
        {
            Description = "Node id(s) to research from (repeatable)",
            Required = true,
            AllowMultipleArgumentsPerToken = true
        };
        var queryTextOpt = new Option<string>("--query")
        {
            Description = "Free-text label describing the research question",
            DefaultValueFactory = _ => ""
        };
        var depthOpt = new Option<int>("--depth") { Description = "Max traversal depth from focus nodes", DefaultValueFactory = _ => 3 };
        var thresholdOpt = new Option<double>("--threshold")
        {
            Description = "Minimum relevance (1/(1+depth)) a node needs to be included",
            DefaultValueFactory = _ => 0.0
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Output research JSON file",
            DefaultValueFactory = _ => "research.json"
        };

        var cmd = new Command("research", "Export a relevance-scored subgraph around focus nodes for LLM consumption");
        cmd.Add(graphArg);
        cmd.Add(focusOpt);
        cmd.Add(queryTextOpt);
        cmd.Add(depthOpt);
        cmd.Add(thresholdOpt);
        cmd.Add(outputOpt);

        cmd.SetAction((parseResult, ct) => RunResearchAsync(
            parseResult.GetValue(graphArg)!,
            parseResult.GetValue(focusOpt)!,
            parseResult.GetValue(queryTextOpt)!,
            parseResult.GetValue(depthOpt),
            parseResult.GetValue(thresholdOpt),
            parseResult.GetValue(outputOpt)!,
            ct));

        return cmd;
    }

    private static async Task<int> RunResearchAsync(
        FileInfo graphFile, string[] focusIds, string queryText, int depth, double threshold,
        string outputPath, CancellationToken ct)
    {
        var graph = await LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;

        var relevance = SeedRelevance(graph, focusIds);
        if (relevance.Count == 0)
        {
            Console.Error.WriteLine("No focus nodes found in the graph; nothing to export.");
            return 1;
        }

        SpreadRelevance(graph, relevance, depth);

        var json = GraphSerializer.ToResearchDocument(graph, relevance, queryText, threshold);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outputPath, json, ct);

        var included = relevance.Count(kv => kv.Value >= threshold);
        Console.WriteLine($"Research document: {included} of {relevance.Count} reached nodes at threshold {threshold}");
        Console.WriteLine($"Output written to {outputPath}");
        return 0;
    }

    private static Dictionary<string, double> SeedRelevance(CodeGraph graph, string[] focusIds)
    {
        var relevance = new Dictionary<string, double>();
        var missing = new List<string>();
        foreach (var id in focusIds)
        {
            if (!graph.HasNode(id)) { missing.Add(id); continue; }
            relevance[id] = 1.0;
        }

        if (missing.Count > 0)
            Console.Error.WriteLine($"Warning: focus node(s) not in graph: {string.Join(", ", missing)}");
        return relevance;
    }

    private static void SpreadRelevance(CodeGraph graph, Dictionary<string, double> relevance, int depth)
    {
        foreach (var id in relevance.Keys.ToList())
        {
            foreach (var (node, _, d) in graph.Traverse(id, edgeFilter: null, maxDepth: depth))
            {
                var score = 1.0 / (1 + d);
                if (!relevance.TryGetValue(node.Id, out var existing) || score > existing)
                    relevance[node.Id] = score;
            }
        }
    }

    private static bool IsSolutionFile(FileInfo path) =>
        path.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
        || path.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    private static async Task WriteGraphAsync(CodeGraph graph, string outputPath, CancellationToken ct)
    {
        var json = GraphSerializer.ToJson(graph);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outputPath, json, ct);

        Console.WriteLine($"Graph built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        Console.WriteLine($"Output written to {outputPath}");
    }

    private static async Task<CodeGraph?> LoadGraphAsync(FileInfo graphFile, CancellationToken ct)
    {
        if (!graphFile.Exists)
        {
            Console.Error.WriteLine($"Graph file not found: {graphFile.FullName}");
            return null;
        }

        var json = await File.ReadAllTextAsync(graphFile.FullName, ct);
        return GraphSerializer.FromJson(json);
    }

    private static string Describe(GraphNode? node) =>
        node == null ? "(not found)" : $"[{node.Type}] {node.Name} ({node.Id})";

    private static void PrintNode(GraphNode node)
    {
        Console.WriteLine($"[{node.Type}] {node.Name}");
        Console.WriteLine($"  Id: {node.Id}");
        Console.WriteLine($"  File: {node.FilePath}");
        if (node.LineStart.HasValue) Console.WriteLine($"  Line: {node.LineStart}");
        if (node.Properties.Count > 0)
        {
            Console.WriteLine("  Properties:");
            foreach (var p in node.Properties)
                Console.WriteLine($"    {p.Key}: {FormatPropertyValue(p.Value)}");
        }
    }

    private static string FormatPropertyValue(object value) =>
        value is System.Collections.IEnumerable items and not string
            ? string.Join(", ", items.Cast<object>())
            : value.ToString() ?? "";

    private static void PrintSummary(CodeGraph graph)
    {
        Console.WriteLine("\n--- Nodes ---");
        foreach (NodeType type in Enum.GetValues<NodeType>())
        {
            var count = graph.NodesOfType(type).Count();
            if (count > 0) Console.WriteLine($"  {type}: {count}");
        }
    }

    private static void PrintEdgeSummary(CodeGraph graph)
    {
        Console.WriteLine("\n--- Edges ---");
        foreach (var group in graph.Edges.GroupBy(e => e.Type).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }
    }
}
