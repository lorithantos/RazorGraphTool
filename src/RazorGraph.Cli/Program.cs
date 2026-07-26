using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;

if (args.Length < 2)
{
    Console.WriteLine("Usage: RazorGraph.Cli <command> <project.csproj> [options]");
    Console.WriteLine("Commands:");
    Console.WriteLine("  build    - Build graph from project and output JSON");
    Console.WriteLine("  query    - Run a query against a built graph");
    Console.WriteLine("");
    Console.WriteLine("Examples:");
    Console.WriteLine("  RazorGraph.Cli build ./MyApp/MyApp.csproj --output graph.json");
    Console.WriteLine("  RazorGraph.Cli query graph.json --type RazorPage --name Catalog");
    return 1;
}

var command = args[0];
var projectPath = args[1];

if (command == "build")
{
    var outputPath = GetArg(args, "--output") ?? "graph.json";

    Console.WriteLine($"Building graph from {projectPath}...");

    var builder = new GraphBuilder();
    var graph = await builder.BuildFromProjectAsync(projectPath);

    var json = GraphSerializer.ToJson(graph);
    await File.WriteAllTextAsync(outputPath, json);

    Console.WriteLine($"Graph built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
    Console.WriteLine($"Output written to {outputPath}");

    // Print summary
    PrintSummary(graph);
    return 0;
}

if (command == "query")
{
    var graphPath = projectPath; // second arg is the graph file for query
    if (!File.Exists(graphPath))
    {
        Console.Error.WriteLine($"Graph file not found: {graphPath}");
        return 1;
    }

    var json = await File.ReadAllTextAsync(graphPath);
    var graph = GraphSerializer.FromJson(json);
    var query = new GraphQuery(graph);

    var nodeType = GetArg(args, "--type");
    var name = GetArg(args, "--name");
    var nodeId = GetArg(args, "--id");
    var neighbors = HasFlag(args, "--neighbors");
    var renderTree = HasFlag(args, "--render-tree");

    if (nodeId != null)
    {
        var node = query.GetNode(nodeId);
        if (node == null)
        {
            Console.WriteLine($"Node not found: {nodeId}");
            return 1;
        }

        PrintNode(node);

        if (neighbors)
        {
            Console.WriteLine("\n--- Outgoing ---");
            foreach (var (edge, target) in query.GetNeighbors(nodeId))
            {
                Console.WriteLine($"  {edge.Type} -> [{target.Type}] {target.Name}");
            }
        }

        if (renderTree && node.Type == NodeType.RazorPage)
        {
            Console.WriteLine("\n--- Render Tree ---");
            foreach (var (n, e) in query.GetRenderTree(nodeId))
            {
                Console.WriteLine($"  {e.Type} -> [{n.Type}] {n.Name}");
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
        Console.WriteLine("Graph loaded. Use --id, --type, or --name to query.");
        Console.WriteLine($"Total: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
    }

    return 0;
}

Console.Error.WriteLine($"Unknown command: {command}");
return 1;

static string? GetArg(string[] args, string flag)
{
    for (int i = 0; i < args.Length - 1; i++)
        if (args[i] == flag) return args[i + 1];
    return null;
}

static bool HasFlag(string[] args, string flag) => args.Contains(flag);

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
            Console.WriteLine($"    {p.Key}: {p.Value}");
    }
}

static void PrintSummary(CodeGraph graph)
{
    Console.WriteLine("\n--- Summary ---");
    foreach (NodeType type in Enum.GetValues<NodeType>())
    {
        var count = graph.NodesOfType(type).Count();
        if (count > 0) Console.WriteLine($"  {type}: {count}");
    }
}
