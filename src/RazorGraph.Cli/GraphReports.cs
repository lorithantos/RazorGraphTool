namespace RazorGraph.Cli;

using RazorGraph.Core.Graph;

/// <summary>
/// Console rendering of graph objects: one node in full, one node in one line,
/// and the node/edge count summaries printed after a build.
/// </summary>
internal static class GraphReports
{
    internal static string Describe(GraphNode? node) =>
        node == null ? "(not found)" : $"[{node.DisplayType}] {node.Name} ({node.Id})";

    internal static void PrintNode(GraphNode node)
    {
        Console.WriteLine($"[{node.DisplayType}] {node.Name}");
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
        // Grouped rather than enumerated over the enum: a foreign vocabulary has
        // no enum members to iterate, and walking NodeType would silently omit
        // every kind this build does not have a name for.
        Console.WriteLine("\n--- Nodes ---");
        PrintCensus(graph.Nodes.GroupBy(n => n.DisplayType));
    }

    internal static void PrintEdgeSummary(CodeGraph graph)
    {
        Console.WriteLine("\n--- Edges ---");
        PrintCensus(graph.Edges.GroupBy(e => e.DisplayType));
    }

    private static void PrintCensus<T>(IEnumerable<IGrouping<string, T>> groups)
    {
        foreach (var group in groups.OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            Console.WriteLine($"  {group.Key}: {group.Count()}");
        }
    }
}
