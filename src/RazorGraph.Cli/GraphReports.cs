namespace RazorGraph.Cli;

using RazorGraph.Core.Graph;

/// <summary>
/// Console rendering of graph objects: one node in full, one node in one line,
/// and the node/edge count summaries printed after a build.
/// </summary>
internal static class GraphReports
{
    internal static string Describe(GraphNode? node) =>
        node == null ? "(not found)" : $"[{node.Type}] {node.Name} ({node.Id})";

    internal static void PrintNode(GraphNode node)
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

    internal static void PrintSummary(CodeGraph graph)
    {
        Console.WriteLine("\n--- Nodes ---");
        foreach (NodeType type in Enum.GetValues<NodeType>())
        {
            var count = graph.NodesOfType(type).Count();
            if (count > 0) Console.WriteLine($"  {type}: {count}");
        }
    }

    internal static void PrintEdgeSummary(CodeGraph graph)
    {
        Console.WriteLine("\n--- Edges ---");
        foreach (var group in graph.Edges.GroupBy(e => e.Type).OrderByDescending(g => g.Count()))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }
    }
}
