namespace RazorGraph.Cli;

using System.CommandLine;
using RazorGraph.Lua;

/// <summary>
/// build-lua: graph a tree of Lua source. Unlike build/build-solution there is no
/// compile step and no MSBuild — the host is detected from what is on disk and the
/// files are parsed directly.
/// </summary>
internal static class LuaCommands
{
    public static Command BuildLua()
    {
        var pathArg = new Argument<DirectoryInfo>("path")
        {
            Description = "Directory containing Lua source (a rockspec or Info.lua selects the host)"
        };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = "Output graph JSON file",
            DefaultValueFactory = _ => "lua-graph.json"
        };
        var showUnresolvedOpt = new Option<int>("--show-unresolved")
        {
            Description = "How many unresolved references to list individually (count is always reported)",
            DefaultValueFactory = _ => 10
        };

        var cmd = new Command("build-lua", "Build a graph from Lua source and output JSON");
        cmd.Add(pathArg);
        cmd.Add(outputOpt);
        cmd.Add(showUnresolvedOpt);

        cmd.SetAction((parseResult, ct) => RunAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(outputOpt)!,
            parseResult.GetValue(showUnresolvedOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunAsync(DirectoryInfo path, string outputPath, int showUnresolved, CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"Directory not found: {path.FullName}");
            return 1;
        }

        Console.WriteLine($"Building Lua graph from {path.FullName}...");

        var (graph, report) = new LuaGraphBuilder().Build(path.FullName);

        // The host is always reported: a misdetected host uses the wrong reference
        // function and yields a confidently empty graph.
        Console.WriteLine($"Host: {report.HostName}  ({report.HostEvidence})");
        Console.WriteLine($"Modules: {report.Modules}  Functions: {report.Functions}");
        Console.WriteLine(
            $"References: {report.ResolvedReferences} resolved, {report.ExternalReferences} external, " +
            $"{report.UnresolvedReferences.Count} unresolved");

        if (report.StructuralCaveat is { } caveat) Console.Error.WriteLine($"note: {caveat}");

        if (report.UnresolvedReferences.Count > 0)
        {
            // Listed, not just counted -- these are the edges the graph does not
            // have, and a bare number cannot be acted on.
            Console.Error.WriteLine($"unresolved references ({report.UnresolvedReferences.Count}):");
            foreach (var u in report.UnresolvedReferences.Take(Math.Max(0, showUnresolved)))
                Console.Error.WriteLine($"  {u}");
            if (report.UnresolvedReferences.Count > showUnresolved)
                Console.Error.WriteLine($"  ... {report.UnresolvedReferences.Count - showUnresolved} more (--show-unresolved to see them)");
        }

        if (report.ParseFailures.Count > 0)
        {
            Console.Error.WriteLine($"parse failures ({report.ParseFailures.Count}):");
            foreach (var f in report.ParseFailures.Take(10)) Console.Error.WriteLine($"  {f}");
            if (report.ParseFailures.Count > 10)
                Console.Error.WriteLine($"  ... {report.ParseFailures.Count - 10} more");
        }

        await GraphFiles.WriteGraphAsync(graph, outputPath, ct);
        GraphReports.PrintSummary(graph);
        GraphReports.PrintEdgeSummary(graph);
        return 0;
    }
}
