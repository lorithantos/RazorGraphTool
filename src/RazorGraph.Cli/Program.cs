using System.CommandLine;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Roslyn;

var root = new RootCommand("RazorGraph — queryable code graph of ASP.NET Core Razor apps");
root.Add(CreateBuildCommand());
root.Add(CreateQueryCommand());
root.Add(CreateResearchCommand());
return await root.Parse(args).InvokeAsync();

static Command CreateBuildCommand()
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

    var cmd = new Command("build", "Build graph from a project or solution and output JSON");
    cmd.Add(pathArg);
    cmd.Add(outputOpt);
    cmd.Add(projectOpt);

    cmd.SetAction(async (parseResult, ct) =>
    {
        var path = parseResult.GetValue(pathArg)!;
        var outputPath = parseResult.GetValue(outputOpt)!;
        var projectName = parseResult.GetValue(projectOpt);

        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        var isSolution = path.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
                      || path.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
        {
            Console.Error.WriteLine("--project <name> is required when building from a solution.");
            return 1;
        }

        RoslynExtractor.EnsureMsBuildRegistered();

        Console.WriteLine($"Building graph from {path.FullName}...");

        await using var builder = new GraphBuilder();
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(path.FullName, projectName!, ct)
            : await builder.BuildFromProjectAsync(path.FullName, ct);

        var json = GraphSerializer.ToJson(graph);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outputPath, json, ct);

        Console.WriteLine($"Graph built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        Console.WriteLine($"Output written to {outputPath}");

        PrintSummary(graph);
        return 0;
    });

    return cmd;
}

static Command CreateQueryCommand()
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
    var mismatchesOpt = new Option<bool>("--mismatches") { Description = "Report server-prepared data consumed by client JS" };

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
    cmd.Add(mismatchesOpt);

    cmd.SetAction(async (parseResult, ct) =>
    {
        var graphFile = parseResult.GetValue(graphArg)!;
        var graph = await LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;
        var query = new GraphQuery(graph);

        var nodeId = parseResult.GetValue(idOpt);
        var nodeType = parseResult.GetValue(typeOpt);
        var name = parseResult.GetValue(nameOpt);

        if (parseResult.GetValue(mismatchesOpt))
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

        if (nodeId != null)
        {
            var node = query.GetNode(nodeId);
            if (node == null)
            {
                Console.WriteLine($"Node not found: {nodeId}");
                return 1;
            }

            PrintNode(node);

            if (parseResult.GetValue(neighborsOpt))
            {
                Console.WriteLine("\n--- Outgoing ---");
                foreach (var (edge, target) in query.GetNeighbors(nodeId))
                {
                    Console.WriteLine($"  {edge.Type} -> [{target.Type}] {target.Name}");
                }
            }

            if (parseResult.GetValue(renderTreeOpt) && node.Type == NodeType.RazorPage)
            {
                Console.WriteLine("\n--- Render Tree ---");
                foreach (var (n, e) in query.GetRenderTree(nodeId))
                {
                    Console.WriteLine($"  {e.Type} -> [{n.Type}] {n.Name}");
                }
            }

            if (parseResult.GetValue(contextOpt))
            {
                Console.WriteLine("\n--- Page Context ---");
                var context = query.GetPageContext(nodeId);
                if (context == null)
                {
                    Console.WriteLine("  Not a Razor page node; no context available.");
                }
                else
                {
                    Console.WriteLine($"  PageModel: {Describe(context.PageModel)}");
                    Console.WriteLine($"  ViewModel: {Describe(context.ViewModel)}");
                    Console.WriteLine($"  Injected services ({context.InjectedServices.Count}):");
                    foreach (var svc in context.InjectedServices)
                        Console.WriteLine($"    [{svc.Type}] {svc.Name}");
                }
            }

            if (parseResult.GetValue(traceOpt))
            {
                Console.WriteLine("\n--- Data Flow ---");
                var depth = parseResult.GetValue(depthOpt);
                foreach (var (n, e, d) in query.TraceDataFlow(nodeId, depth))
                {
                    Console.WriteLine($"  {new string(' ', d * 2)}{e.Type} -> [{n.Type}] {n.Name}");
                }
            }
        }
        else if (nodeType != null && Enum.TryParse<NodeType>(nodeType, true, out var type))
        {
            var nodes = query.FindNodes(type, name).ToList();
            Console.WriteLine($"Found {nodes.Count} nodes of type {type}:");
            foreach (var n in nodes)
            {
                Console.WriteLine($"  [{n.Id}] {n.Name} ({n.FilePath})");
            }
        }
        else
        {
            Console.WriteLine("Graph loaded. Use --id, --type, or --mismatches to query.");
            Console.WriteLine($"Total: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        }

        return 0;
    });

    return cmd;
}

static Command CreateResearchCommand()
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

    cmd.SetAction(async (parseResult, ct) =>
    {
        var graphFile = parseResult.GetValue(graphArg)!;
        var graph = await LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;

        var focusIds = parseResult.GetValue(focusOpt)!;
        var depth = parseResult.GetValue(depthOpt);
        var threshold = parseResult.GetValue(thresholdOpt);
        var queryText = parseResult.GetValue(queryTextOpt)!;
        var outputPath = parseResult.GetValue(outputOpt)!;

        var relevance = new Dictionary<string, double>();
        var missing = new List<string>();
        foreach (var id in focusIds)
        {
            if (!graph.HasNode(id)) { missing.Add(id); continue; }
            relevance[id] = 1.0;
        }

        if (missing.Count > 0)
            Console.Error.WriteLine($"Warning: focus node(s) not in graph: {string.Join(", ", missing)}");
        if (relevance.Count == 0)
        {
            Console.Error.WriteLine("No focus nodes found in the graph; nothing to export.");
            return 1;
        }

        foreach (var id in relevance.Keys.ToList())
        {
            foreach (var (node, _, d) in graph.Traverse(id, edgeFilter: null, maxDepth: depth))
            {
                var score = 1.0 / (1 + d);
                if (!relevance.TryGetValue(node.Id, out var existing) || score > existing)
                    relevance[node.Id] = score;
            }
        }

        var json = GraphSerializer.ToResearchDocument(graph, relevance, queryText, threshold);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outputPath, json, ct);

        var included = relevance.Count(kv => kv.Value >= threshold);
        Console.WriteLine($"Research document: {included} of {relevance.Count} reached nodes at threshold {threshold}");
        Console.WriteLine($"Output written to {outputPath}");
        return 0;
    });

    return cmd;
}

static async Task<CodeGraph?> LoadGraphAsync(FileInfo graphFile, CancellationToken ct)
{
    if (!graphFile.Exists)
    {
        Console.Error.WriteLine($"Graph file not found: {graphFile.FullName}");
        return null;
    }

    var json = await File.ReadAllTextAsync(graphFile.FullName, ct);
    return GraphSerializer.FromJson(json);
}

static string Describe(GraphNode? node) =>
    node == null ? "(not found)" : $"[{node.Type}] {node.Name} ({node.Id})";

static void PrintNode(GraphNode node)
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

static string FormatPropertyValue(object value) =>
    value is System.Collections.IEnumerable items and not string
        ? string.Join(", ", items.Cast<object>())
        : value.ToString() ?? "";

static void PrintSummary(CodeGraph graph)
{
    Console.WriteLine("\n--- Summary ---");
    foreach (NodeType type in Enum.GetValues<NodeType>())
    {
        var count = graph.NodesOfType(type).Count();
        if (count > 0) Console.WriteLine($"  {type}: {count}");
    }
}
