namespace RazorGraph.Cli;

using System.CommandLine;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Attributes;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// The two commands that compile source into a graph: build (one project) and
/// build-solution (every project, with cross-project edges).
/// </summary>
internal static class BuildCommands
{
    public static Command Build()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .csproj or .sln file" };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = $"Output graph JSON file (default: inside {GraphFiles.OutputDirectory}\\)",
            DefaultValueFactory = _ => GraphFiles.DefaultOutput("graph.json")
        };
        var projectOpt = new Option<string?>("--project")
        {
            Description = "Project name inside the solution (required when path is a .sln)"
        };
        var includeVendorOpt = new Option<bool>("--include-vendor")
        {
            Description = "Also graph vendor/minified client assets (dropped by default); their nodes carry vendor=true"
        };
        var policyOpt = AttributePolicyOption();

        var cmd = new Command("build", "Build graph from a project or solution and output JSON");
        cmd.Add(pathArg);
        cmd.Add(outputOpt);
        cmd.Add(projectOpt);
        cmd.Add(includeVendorOpt);
        cmd.Add(policyOpt);

        cmd.SetAction((parseResult, ct) => RunBuildAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(outputOpt)!,
            parseResult.GetValue(projectOpt),
            parseResult.GetValue(includeVendorOpt),
            parseResult.GetValue(policyOpt),
            ct));

        return cmd;
    }

    private static Option<FileInfo?> AttributePolicyOption() => new("--attribute-policy")
    {
        Description = "Attribute policy JSON overriding the embedded default (classification name sets, "
            + "argument-payload suppression, registration vocabulary). Sections the file omits are inherited."
    };

    /// <summary>
    /// Resolve the policy for this run — the disk override, or the embedded
    /// default. A named file that does not exist is an error, not a fallback:
    /// silently building with the default would answer with the wrong policy.
    /// </summary>
    private static AttributePolicy? LoadPolicyOrReport(FileInfo? policyFile)
    {
        if (policyFile == null) return AttributePolicy.Default;
        if (!policyFile.Exists)
        {
            Console.Error.WriteLine($"Attribute policy file not found: {policyFile.FullName}");
            return null;
        }
        return AttributePolicy.LoadFile(policyFile.FullName);
    }

    private static async Task<int> RunBuildAsync(
        FileInfo path, string outputPath, string? projectName, bool includeVendor, FileInfo? policyFile,
        CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        var isSolution = GraphFiles.IsSolutionFile(path);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
        {
            Console.Error.WriteLine("--project <name> is required when building from a solution.");
            return 1;
        }

        if (LoadPolicyOrReport(policyFile) is not { } policy) return 1;

        RoslynExtractor.EnsureMsBuildRegistered();

        Console.WriteLine($"Building graph from {path.FullName}...");

        await using var builder = new GraphBuilder { IncludeVendorAssets = includeVendor, AttributePolicy = policy };
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(path.FullName, projectName!, ct)
            : await builder.BuildFromProjectAsync(path.FullName, ct);

        await GraphFiles.WriteGraphAsync(graph, outputPath, ct);

        // Findings ride the build output; a finding that must be queried for is
        // a secret. Stderr, because it is a diagnosis rather than a result.
        foreach (var shape in builder.UnboundShapes)
            Console.Error.WriteLine($"warning: unbound shape: {shape} — no template, Liquid file or code binding serves this name; OrchardCore throws at render time.");

        ReportAnalyzerHostWarnings(builder);
        ReportUnresolvedAttributes(builder);

        GraphReports.PrintSummary(graph);
        return 0;
    }

    /// <summary>
    /// An SDK generator that did not load produces no types and no error. This
    /// is the only place that says so, which is why it rides the build output
    /// beside the other findings rather than waiting to be asked for.
    /// </summary>
    private static void ReportAnalyzerHostWarnings(GraphBuilder builder)
    {
        foreach (var warning in builder.AnalyzerHostWarnings)
            Console.Error.WriteLine($"warning: {warning}");
    }

    /// <summary>
    /// C# binds attribute types at compile time, so an unbound one means the
    /// compilation had errors — which makes every other answer in this graph less
    /// trustworthy, not just the attribute edges. Rides the build output for the
    /// same reason unbound shapes do: a finding that has to be queried for is a
    /// secret.
    /// </summary>
    private static void ReportUnresolvedAttributes(GraphBuilder builder)
    {
        if (builder.UnresolvedAttributes.Count == 0) return;

        Console.Error.WriteLine(
            $"warning: {builder.UnresolvedAttributes.Count} attribute(s) did not resolve — the solution did not compile cleanly, " +
            "so this graph is missing edges it cannot know about:");
        foreach (var unresolved in builder.UnresolvedAttributes)
            Console.Error.WriteLine($"  {unresolved}");
    }

    public static Command BuildSolution()
    {
        var pathArg = new Argument<FileInfo>("path") { Description = "Path to a .sln or .slnx file" };
        var outputOpt = new Option<string>("--output", "-o")
        {
            Description = $"Output graph JSON file (default: inside {GraphFiles.OutputDirectory}\\)",
            DefaultValueFactory = _ => GraphFiles.DefaultOutput("solution-graph.json")
        };

        var includeVendorOpt = new Option<bool>("--include-vendor")
        {
            Description = "Also graph vendor/minified client assets (dropped by default); their nodes carry vendor=true"
        };
        var noTestsOpt = new Option<bool>("--no-tests")
        {
            Description = "Skip test projects: no test Method nodes, no Covers edges. Coverage queries against the resulting graph refuse to answer rather than reporting everything uncovered."
        };
        var policyOpt = AttributePolicyOption();

        var cmd = new Command("build-solution",
            "Build ONE graph spanning every project in a solution, with edges that cross project boundaries");
        cmd.Add(pathArg);
        cmd.Add(outputOpt);
        cmd.Add(includeVendorOpt);
        cmd.Add(noTestsOpt);
        cmd.Add(policyOpt);

        cmd.SetAction((parseResult, ct) => RunBuildSolutionAsync(
            parseResult.GetValue(pathArg)!,
            parseResult.GetValue(outputOpt)!,
            parseResult.GetValue(includeVendorOpt),
            parseResult.GetValue(noTestsOpt),
            parseResult.GetValue(policyOpt),
            ct));

        return cmd;
    }

    private static async Task<int> RunBuildSolutionAsync(
        FileInfo path, string outputPath, bool includeVendor, bool noTests, FileInfo? policyFile,
        CancellationToken ct)
    {
        if (!path.Exists)
        {
            Console.Error.WriteLine($"File not found: {path.FullName}");
            return 1;
        }

        if (!GraphFiles.IsSolutionFile(path))
        {
            Console.Error.WriteLine($"Not a solution file: {path.FullName}. Use 'build' for a .csproj.");
            return 1;
        }

        if (LoadPolicyOrReport(policyFile) is not { } policy) return 1;

        RoslynExtractor.EnsureMsBuildRegistered();
        Console.WriteLine($"Building solution graph from {path.FullName}...");

        await using var builder = new GraphBuilder
        {
            IncludeVendorAssets = includeVendor,
            ExcludeTestProjects = noTests,
            AttributePolicy = policy
        };
        var graph = await builder.BuildFromSolutionAllAsync(path.FullName, ct);

        await GraphFiles.WriteGraphAsync(graph, outputPath, ct);

        var projects = graph.NodesOfType(NodeType.Project).Select(p => p.Name).OrderBy(n => n).ToList();
        if (projects.Count > 0)
            Console.WriteLine($"Projects: {string.Join(", ", projects)}");
        if (builder.SkippedTestProjects.Count > 0)
            Console.WriteLine($"Test projects skipped: {string.Join(", ", builder.SkippedTestProjects)}");
        foreach (var shape in builder.UnboundShapes)
            Console.Error.WriteLine($"warning: unbound shape: {shape} — no template, Liquid file or code binding serves this name; OrchardCore throws at render time.");

        ReportAnalyzerHostWarnings(builder);
        ReportUnresolvedAttributes(builder);

        GraphReports.PrintSummary(graph);
        GraphReports.PrintEdgeSummary(graph);
        return 0;
    }
}
