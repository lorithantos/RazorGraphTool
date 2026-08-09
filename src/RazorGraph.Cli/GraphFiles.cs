namespace RazorGraph.Cli;

using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;

/// <summary>
/// The CLI's file boundary: what comes in (project/solution paths, saved graph
/// JSON) and what goes out (graph JSON). Every command that touches disk goes
/// through here.
/// </summary>
internal static class GraphFiles
{
    internal static bool IsSolutionFile(FileInfo path) =>
        path.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
        || path.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    internal static async Task WriteGraphAsync(CodeGraph graph, string outputPath, CancellationToken ct)
    {
        var json = GraphSerializer.ToJson(graph);
        var outputDir = Path.GetDirectoryName(Path.GetFullPath(outputPath));
        if (!string.IsNullOrEmpty(outputDir)) Directory.CreateDirectory(outputDir);
        await File.WriteAllTextAsync(outputPath, json, ct);

        Console.WriteLine($"Graph built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        Console.WriteLine($"Output written to {outputPath}");
    }

    internal static async Task<CodeGraph?> LoadGraphAsync(FileInfo graphFile, CancellationToken ct)
    {
        if (!graphFile.Exists)
        {
            Console.Error.WriteLine($"Graph file not found: {graphFile.FullName}");
            return null;
        }

        var json = await File.ReadAllTextAsync(graphFile.FullName, ct);
        return GraphSerializer.FromJson(json);
    }
}
