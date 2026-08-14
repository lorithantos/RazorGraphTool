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
    /// <summary>The directory every default output goes in. See <see cref="GraphOutput"/>.</summary>
    internal const string OutputDirectory = GraphOutput.Directory;

    /// <summary>A default output path inside <see cref="OutputDirectory"/>.</summary>
    internal static string DefaultOutput(string fileName) => GraphOutput.Default(fileName);

    internal static bool IsSolutionFile(FileInfo path) =>
        path.Extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
        || path.Extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Creates the output directory. See <see cref="GraphOutput.Prepare"/>.</summary>
    internal static void PrepareOutput(string outputPath) => GraphOutput.Prepare(outputPath);

    /// <summary>
    /// Prints the git advice for a path just written, if there is any.
    /// </summary>
    /// <remarks>
    /// stderr, because it is a note about the caller's repository rather than part of the
    /// result, and a command whose stdout is piped somewhere should not have this in the pipe.
    /// </remarks>
    internal static void ReportGitAdvice(string outputPath)
    {
        if (GraphOutput.GitAdvice(outputPath) is { } advice) Console.Error.WriteLine(advice);
    }

    internal static async Task WriteGraphAsync(CodeGraph graph, string outputPath, CancellationToken ct)
    {
        var json = GraphSerializer.ToJson(graph);
        PrepareOutput(outputPath);
        await File.WriteAllTextAsync(outputPath, json, ct);

        Console.WriteLine($"Graph built: {graph.Nodes.Count} nodes, {graph.Edges.Count} edges");
        Console.WriteLine($"Output written to {outputPath}");
        ReportGitAdvice(outputPath);
    }

    internal static async Task<CodeGraph?> LoadGraphAsync(FileInfo graphFile, CancellationToken ct)
    {
        if (!graphFile.Exists)
        {
            Console.Error.WriteLine($"Graph file not found: {graphFile.FullName}");
            return null;
        }

        var json = await File.ReadAllTextAsync(graphFile.FullName, ct);

        GraphReadResult read;
        try
        {
            read = GraphSerializer.Read(json);
        }
        catch (InvalidOperationException ex)
        {
            // A format the reader will not accept is a contract refusal, and the
            // refusal IS the report -- same shape as the missing-file case above.
            // Letting it escape as an unhandled exception buries the explanation
            // in a stack trace and reads as a crash.
            Console.Error.WriteLine($"error: {ex.Message}");
            return null;
        }

        // stderr, not stdout: query output is parsed by callers, and a caveat is
        // not a result. Unconditional when present -- a warning behind a flag is
        // not a warning.
        if (read.Format.Caveat is { } caveat) Console.Error.WriteLine($"warning: {caveat}");

        return read.Graph;
    }
}
