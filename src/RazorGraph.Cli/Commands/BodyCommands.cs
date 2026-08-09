namespace RazorGraph.Cli;

using System.CommandLine;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// The two commands that compile a project to look inside one method: body
/// (emit its control-flow graph) and body-diff (prove two bodies equivalent).
/// </summary>
internal static class BodyCommands
{
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
        await LoadCompilationAsync(roslyn, path, projectName, ct);

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
        await LoadCompilationAsync(roslyn, path, projectName, ct);

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

    /// <summary>
    /// Both body commands compile the same three ways: whole solution, one
    /// project of a solution, or a lone project. This dispatch lived twice,
    /// byte-identical, before the commands moved here.
    /// </summary>
    private static async Task LoadCompilationAsync(
        RoslynExtractor roslyn, FileInfo path, string? projectName, CancellationToken ct)
    {
        if (GraphFiles.IsSolutionFile(path))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(path.FullName, ct: ct);
            else
                await roslyn.LoadSolutionAsync(path.FullName, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(path.FullName, ct);
        }
    }
}
