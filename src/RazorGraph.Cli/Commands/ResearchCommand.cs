namespace RazorGraph.Cli;

using System.CommandLine;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;

/// <summary>
/// The research command: export a relevance-scored subgraph around focus nodes
/// for LLM consumption.
/// </summary>
internal static class ResearchCommand
{
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
            Description = $"Output research JSON file (default: inside {GraphFiles.OutputDirectory}\\)",
            DefaultValueFactory = _ => GraphFiles.DefaultOutput("research.json")
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
        var graph = await GraphFiles.LoadGraphAsync(graphFile, ct);
        if (graph == null) return 1;

        // Scoring lives in Core (GraphQuery.ComputeRelevance) — shared with
        // the MCP twin. Only the missing-focus policy is this front end's:
        // warn and continue with what resolved, fail only if nothing did.
        var (relevance, missing) = new GraphQuery(graph).ComputeRelevance(focusIds, depth);
        if (missing.Count > 0)
            Console.Error.WriteLine($"Warning: focus node(s) not in graph: {string.Join(", ", missing)}");
        if (relevance.Count == 0)
        {
            Console.Error.WriteLine("No focus nodes found in the graph; nothing to export.");
            return 1;
        }

        var json = GraphSerializer.ToResearchDocument(graph, relevance, queryText, threshold);
        GraphFiles.PrepareOutput(outputPath);
        await File.WriteAllTextAsync(outputPath, json, ct);

        var included = relevance.Count(kv => kv.Value >= threshold);
        Console.WriteLine($"Research document: {included} of {relevance.Count} reached nodes at threshold {threshold}");
        Console.WriteLine($"Output written to {outputPath}");
        GraphFiles.ReportGitAdvice(outputPath);
        return 0;
    }
}
