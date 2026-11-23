namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;
using RazorGraph.Lua;

/// <summary>
/// Tools that manage the graphs the server holds: build them from source, load
/// and save them as JSON, list, summarize, and drop them.
/// </summary>
[McpServerToolType]
internal sealed class GraphStoreTools(GraphStore store)
{
    /// <summary>
    /// Where every successful build is cached. The per-user default in production;
    /// tests point it at a scratch directory so they neither read nor litter the real one.
    /// </summary>
    internal string? CacheDirectory { get; init; } = GraphCache.DefaultDirectory;

    /// <summary>
    /// Registers a built graph and saves it to the cache: the one step all three build
    /// tools share, and the only one -- load_graph registers too, but what it loads is
    /// already on disk.
    /// </summary>
    private (GraphStore.GraphEntry Entry, GraphCache.Outcome Cache) AddBuilt(CodeGraph graph, string source, string? graphId)
    {
        var entry = store.Add(graph, source, graphId);
        return (entry, GraphCache.Save(entry, CacheDirectory));
    }

    [McpServerTool(Name = "build_graph")]
    [Description("Build a code graph from one ASP.NET Core Razor project (.csproj), or one project inside a solution. Slow (compiles the project). To graph a whole solution with edges that cross project boundaries, use build_solution instead. Returns a summary with node/edge counts by type, per-phase timings as phases, and projectFailures when the project did not load or compile cleanly. Sends progress notifications when the request carries a progressToken. The graph is also saved to the server's cache, reported as savedTo (or saveError), and load_graph on that path restores it in a later session without recompiling.")]
    public async Task<string> BuildGraph(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Project name inside the solution; required when path is a solution")] string? projectName = null,
        [Description("Id to file the result under. Defaults to the file name; reusing an id replaces that graph.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        [Description(ExcludeLiteralsDescription)] bool excludeLiterals = false,
        [Description("Absolute path to an attribute-policy JSON overriding the embedded default (classification name sets, argument-payload suppression, registration vocabulary). Sections the file omits are inherited.")] string? attributePolicy = null,
        [Description(PlatformDescription)] string? platform = null,
        [Description(ExcludeTextDescription)] bool excludeText = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var isSolution = ToolArguments.IsSolution(full);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
            throw new McpException(
                "projectName is required when building a single project from a solution. " +
                "To graph every project at once, call build_solution.");

        await using var builder = new GraphBuilder
        {
            IncludeVendorAssets = includeVendor,
            ExcludeLiterals = excludeLiterals,
            ExcludeText = excludeText,
            AttributePolicy = LoadPolicy(attributePolicy),
            Platform = platform,
            Progress = BuildProgressNotifications.Forward(progress)
        };
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(full, projectName!, ct)
            : await builder.BuildFromProjectAsync(full, ct);

        var (entry, cache) = AddBuilt(graph, full, graphId);
        return Summarize(entry, builder.AssetSkipSummaries,
            unboundShapes: builder.UnboundShapes, unresolvedAttributes: builder.UnresolvedAttributes,
            analyzerHostWarnings: builder.AnalyzerHostWarnings, literalsExcluded: excludeLiterals,
            textExcluded: excludeText, textFileSkips: builder.TextFileSkips, build: BuildReport.From(builder),
            cache: cache);
    }

    /// <summary>
    /// What a build response says about the build itself rather than the graph:
    /// how many projects it was asked for, where the time went, and which
    /// projects failed. Present on build responses only -- a loaded graph's build
    /// happened somewhere else and this server cannot vouch for it.
    /// </summary>
    private sealed record BuildReport(int ProjectCount, IReadOnlyList<BuildPhaseTiming> Phases, IReadOnlyList<string> ProjectFailures)
    {
        public static BuildReport From(GraphBuilder builder) =>
            new(builder.ProjectCount, builder.PhaseTimings, builder.ProjectFailures);
    }

    /// <summary>
    /// Shared between build_graph and build_solution so the two cannot describe the
    /// same switch differently.
    /// </summary>
    private const string ExcludeLiteralsDescription =
        "Skip the literal index: no Literal nodes, no Mentions edges, so find_literals returns nothing " +
        "and \"where does this string appear\" is unanswerable against the result. Default false, so the " +
        "default graph answers every question. Quotes edges and quoted_symbols are unaffected either way — " +
        "they are a different question over the same scan. A build that used this says so in its summary.";

    /// <summary>Shared between build_graph and build_solution, as ExcludeLiteralsDescription is.</summary>
    private const string ExcludeTextDescription =
        "Skip the text-file index: no TextFile nodes, so grep's graph section answers for C# names and " +
        "literals alone and every line of a .json, .md, .ps1, .bicep, .csproj or other non-C# file comes " +
        "from its disk section. Default false, so the default graph answers every question. A build that " +
        "used this says so in its summary.";

    /// <summary>
    /// Shared for the same reason. Omitting it must stay the old behaviour, which
    /// is why the description says what omitting does rather than calling it a
    /// default: the server's environment decides, and a restart can change that.
    /// </summary>
    private const string PlatformDescription =
        "Passed to MSBuild as the Platform global property, e.g. x64, AnyCPU, ARM64. Omit it to inherit " +
        "the server's environment, as builds always have; the graph records which happened as platform " +
        "and platformFrom (requested, environment or projectDefault).";

    [McpServerTool(Name = "build_solution")]
    [Description("Build ONE graph spanning every project in a solution (.sln/.slnx). Unlike build_graph, calls and injections that cross project boundaries become real edges — this is what makes \"which tests cover this method\" and \"what breaks if I change this\" answerable. Adds Project nodes, per-node project attribution, and Covers edges from test methods to the code they exercise. Slower than build_graph: it compiles every project. Sends progress notifications (phase, and project N of M) when the request carries a progressToken; the response always carries projectCount, per-phase timings as phases, and projectFailures naming any project that did not load or compile, with its first errors -- the graph is still built from what did. The graph is also saved to the server's cache, reported as savedTo (or saveError), and load_graph on that path restores it in a later session without recompiling.")]
    public async Task<string> BuildSolution(
        [Description("Absolute path to a .sln or .slnx file")] string path,
        [Description("Id to file the result under. Defaults to the solution file name.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        [Description("Skip test projects: no test Method nodes, no Covers edges — a leaner navigation graph. Coverage tools refuse to answer against it rather than reporting everything uncovered. Default false, so the default graph answers every question.")] bool excludeTests = false,
        [Description(ExcludeLiteralsDescription)] bool excludeLiterals = false,
        [Description("Absolute path to an attribute-policy JSON overriding the embedded default (classification name sets, argument-payload suppression, registration vocabulary). Sections the file omits are inherited.")] string? attributePolicy = null,
        [Description(PlatformDescription)] string? platform = null,
        [Description(ExcludeTextDescription)] bool excludeText = false,
        IProgress<ProgressNotificationValue>? progress = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");
        if (!ToolArguments.IsSolution(full))
            throw new McpException($"Not a solution file: {full}. Use build_graph for a .csproj.");

        await using var builder = new GraphBuilder
        {
            IncludeVendorAssets = includeVendor,
            ExcludeTestProjects = excludeTests,
            ExcludeLiterals = excludeLiterals,
            ExcludeText = excludeText,
            AttributePolicy = LoadPolicy(attributePolicy),
            Platform = platform,
            Progress = BuildProgressNotifications.Forward(progress)
        };
        var graph = await builder.BuildFromSolutionAllAsync(full, ct);

        var (entry, cache) = AddBuilt(graph, full, graphId);
        return Summarize(entry, builder.AssetSkipSummaries, builder.SkippedTestProjects,
            builder.UnboundShapes, unresolvedAttributes: builder.UnresolvedAttributes,
            analyzerHostWarnings: builder.AnalyzerHostWarnings, literalsExcluded: excludeLiterals,
            textExcluded: excludeText, textFileSkips: builder.TextFileSkips, build: BuildReport.From(builder),
            cache: cache);
    }

    /// <summary>
    /// The policy for one build: the named override, or the embedded default. A
    /// named file that does not exist is an error — silently building with the
    /// default would answer with the wrong policy.
    /// </summary>
    private static RazorGraph.Extractor.Attributes.AttributePolicy LoadPolicy(string? attributePolicy)
    {
        if (attributePolicy == null) return RazorGraph.Extractor.Attributes.AttributePolicy.Default;
        var full = Path.GetFullPath(attributePolicy);
        if (!File.Exists(full)) throw new McpException($"Attribute policy file not found: {full}");
        return RazorGraph.Extractor.Attributes.AttributePolicy.LoadFile(full);
    }

    [McpServerTool(Name = "build_lua")]
    [Description("Build a graph from a directory of Lua source. Unlike build_graph there is no compile step: the host environment is detected from the tree (Info.lua → Lightroom, .rockspec → LuaRocks, else plain path conventions) and files are parsed directly, so it is fast. Modules and module references are foreign kinds ('luaModule', 'requires') selectable by name; functions are ordinary Method nodes with Contains edges, so normal navigation works. The response reports the detected host, and counts resolved / external / unresolved references separately — external means a host or standard-library module outside the tree, which is not a failure. The graph is also saved to the server's cache, reported as savedTo (or saveError), and load_graph on that path restores it in a later session.")]
    public string BuildLua(
        [Description("Absolute path to a directory containing Lua source")] string path,
        [Description("Id to file the result under. Defaults to the directory name.")] string? graphId = null,
        [Description("How many unresolved references to list individually; the count is always reported. Default 20.")] int unresolvedLimit = 20,
        [Description("Also graph vendor code such as an SDK's own sample plugins (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when reading the vendor's examples rather than your own code.")] bool includeVendor = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!Directory.Exists(full)) throw new McpException($"Directory not found: {full}");

        var (graph, report) = new LuaGraphBuilder { IncludeVendor = includeVendor }.Build(full);
        var (entry, cache) = AddBuilt(graph, full, graphId ?? new DirectoryInfo(full).Name);

        var listed = report.UnresolvedReferences.Take(Math.Max(0, unresolvedLimit)).ToList();

        return ToolResponses.ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            savedTo = cache.SavedTo,
            saveError = cache.SaveError,
            // Always present: a misdetected host uses the wrong reference function
            // and yields a graph that is empty for a reason nothing else reveals.
            host = report.HostName,
            hostEvidence = report.HostEvidence,
            nodes = graph.Nodes.Count,
            edges = graph.Edges.Count,
            modules = report.Modules,
            functions = report.Functions,
            references = new
            {
                resolved = report.ResolvedReferences,
                external = report.ExternalReferences,
                unresolved = report.UnresolvedReferences.Count
            },
            // Listed, not merely counted: these are the edges the graph does not
            // have, and a bare number cannot be acted on.
            unresolvedReferences = listed.Count > 0 ? listed : null,
            unresolvedTruncated = report.UnresolvedReferences.Count > listed.Count,
            parseFailures = report.ParseFailures.Count > 0 ? report.ParseFailures : null,
            // Set when the host has no module-reference mechanism at all, so that
            // an empty module graph reads as a property of the host rather than a
            // gap in extraction.
            structuralCaveat = report.StructuralCaveat,
            // Same contract as build_solution's skippedVendorAssets: a silent skip
            // would read as "that is all the code there is".
            skippedVendorFiles = report.SkippedVendorFiles.Count > 0 ? report.SkippedVendorFiles.Count : (int?)null
        });
    }

    [McpServerTool(Name = "load_graph")]
    [Description("Load a previously built graph from a JSON file (produced by save_graph or the RazorGraph CLI). Fast; adds it alongside any graphs already loaded. The graph keeps the source it was extracted from; the file is reported as loadedFrom.")]
    public async Task<string> LoadGraph(
        [Description("Path to a graph JSON file")] string path,
        [Description("Id to file the result under. Defaults to the file name.")] string? graphId = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var read = GraphSerializer.Read(await File.ReadAllTextAsync(full, ct));

        // The source the file records, so a graph carried through a save keeps
        // naming its solution. A file written before that field has nothing to
        // offer, and the file itself is the only origin there is to report.
        return Summarize(store.Add(read.Graph, read.Graph.Source ?? full, graphId, loadedFrom: full), format: read.Format);
    }

    [McpServerTool(Name = "save_graph")]
    [Description("Save a loaded graph to a JSON file so future sessions can load_graph instead of rebuilding.")]
    public async Task<string> SaveGraph(
        [Description("Output path for the graph JSON file")] string outputPath,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null,
        CancellationToken ct = default)
    {
        var entry = store.Require(graphId);
        var full = Path.GetFullPath(outputPath);
        GraphOutput.Prepare(full);
        await File.WriteAllTextAsync(full, GraphSerializer.ToJson(entry.Graph), ct);

        // Present only when there is something to act on, so its presence IS the signal. An
        // always-there field reading "nothing to do" is one the reader learns to skip.
        return ToolResponses.ToJson(new
        {
            saved = full,
            graphId = entry.Id,
            nodes = entry.Graph.Nodes.Count,
            edges = entry.Graph.Edges.Count,
            gitAdvice = GraphOutput.GitAdvice(full),
        });
    }

    [McpServerTool(Name = "list_graphs")]
    [Description("Every graph currently held by the server: id, source, when it was BUILT and when it was loaded, and its size. Cheap. The 'current' one is what tools use when graphId is omitted. builtAt is the one that matters for trusting an answer — loadedAt only says when it entered this server, so a graph loaded from a saved file a minute ago can have been built days before. builtAt is null on a graph saved before the stamp existed, which means \"cannot tell\", never \"fresh\". extractorBuiltAt is the other half: it says which extractor produced the graph rather than when extraction ran, so a graph rebuilt seconds ago can still carry answers from a binary older than the working tree. Null there means \"cannot tell\" too, never \"current\". source is what the graph was extracted from (its .sln, .slnx, .csproj or Lua directory) and survives save_graph and load_graph; loadedFrom is the file load_graph read it from, absent for a graph built here. A graph saved before source was recorded reports that file as both. hostArchitecture is the CPU that built the graph; platform is the MSBuild Platform it was built with and platformFrom says why -- requested, inherited from the server's environment, or projectDefault, where platform is absent. Generated-code paths depend on it, so compare those before reading a difference between two graphs as a code change. All three absent means a graph saved before they were recorded: \"cannot tell\", never projectDefault.")]
    public string ListGraphs()
    {
        var entries = store.List().Select(e => new
        {
            graphId = e.Id,
            source = e.Source,
            // Separate from source on purpose: source is what the graph was
            // extracted from, which is what a caller matches against a repository;
            // this is only the file it passed through. Absent for a built graph.
            loadedFrom = e.LoadedFrom,
            // Both, because they answer different questions and the obvious one
            // answers the wrong question: load_graph of a week-old saved graph
            // reports loadedAt as seconds ago, which reads as fresh.
            builtAt = e.Graph.BuiltAt,
            extractorBuiltAt = e.Graph.ExtractorBuiltAt,
            // What the graph was built for. Two graphs of one source can differ
            // only in these, and a diff that cannot see them reads as a code change.
            hostArchitecture = e.Graph.HostArchitecture,
            platform = e.Graph.Platform,
            platformFrom = e.Graph.PlatformFrom,
            loadedAt = e.LoadedAt,
            nodes = e.Graph.Nodes.Count,
            edges = e.Graph.Edges.Count,
            isCurrent = string.Equals(e.Id, store.CurrentId, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return ToolResponses.ToJson(new { returned = entries.Count, current = store.CurrentId, graphs = entries });
    }

    [McpServerTool(Name = "drop_graph")]
    [Description("Forget a loaded graph, freeing its memory. Returns dropped=false when no graph had that id.")]
    public string DropGraph([Description("Id of the graph to drop")] string graphId) =>
        ToolResponses.ToJson(new { dropped = store.Remove(graphId), graphId });

    [McpServerTool(Name = "graph_summary")]
    [Description("Summary of one graph: source path, node/edge counts by type, and both build stamps. Cheap orientation call. builtAt is when extraction ran; extractorBuiltAt is when the extractor that ran it was itself built, so a graph rebuilt seconds ago can still carry answers from an older binary. Either is null when it cannot be told, which is never \"fresh\". hostArchitecture, platform and platformFrom say what the graph was built for, as list_graphs describes.")]
    public string GraphSummary([Description(ToolArguments.GraphIdDescription)] string? graphId = null) =>
        Summarize(store.Require(graphId));

    private static string Summarize(
        GraphStore.GraphEntry entry,
        IReadOnlyList<string>? vendorSkips = null,
        IReadOnlyList<string>? testProjectSkips = null,
        IReadOnlyList<string>? unboundShapes = null,
        GraphFormatAssessment? format = null,
        IReadOnlyList<string>? unresolvedAttributes = null,
        IReadOnlyList<string>? analyzerHostWarnings = null,
        bool literalsExcluded = false,
        bool textExcluded = false,
        IReadOnlyList<string>? textFileSkips = null,
        BuildReport? build = null,
        GraphCache.Outcome? cache = null)
    {
        var graph = entry.Graph;
        // Grouped by display kind, so a foreign vocabulary is censused under its
        // own names. Collapsing it into one "Unknown" bucket would hide both what
        // is in the graph and how much of it this build cannot reason about.
        var nodeCounts = graph.Nodes.GroupBy(n => n.DisplayType)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());
        var edgeCounts = graph.Edges.GroupBy(e => e.DisplayType)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key, g => g.Count());

        var projects = graph.NodesOfType(NodeType.Project).Select(n => n.Name).OrderBy(n => n).ToList();

        return ToolResponses.ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            loadedFrom = entry.LoadedFrom,
            // Build responses only: where the server cached this graph, so a later
            // session can load_graph it, or the one line saying why it could not.
            savedTo = cache?.SavedTo,
            saveError = cache?.SaveError,
            // Both stamps, on every response that returns a summary: a build
            // response is the first thing a caller sees, and it must say which
            // extractor answered, not only that extraction just ran.
            builtAt = graph.BuiltAt,
            extractorBuiltAt = graph.ExtractorBuiltAt,
            hostArchitecture = graph.HostArchitecture,
            platform = graph.Platform,
            platformFrom = graph.PlatformFrom,
            // Present on load responses: which format the file on disk carried.
            // A freshly built graph is this build's format by construction, so
            // only a load has something to report.
            formatVersion = format?.Display,
            formatCaveat = format?.Caveat,
            // Before the counts, so a reader meets it first: a partial graph's counts
            // are smaller for a reason, and this is the reason. Root failures lead,
            // each project named with its first few errors as file:line.
            projectFailures = build?.ProjectFailures is { Count: > 0 } failures ? failures : null,
            nodes = graph.Nodes.Count,
            edges = graph.Edges.Count,
            // The M of "project N of M": projects the build was asked for, counting
            // any it skipped, where projects lists the ones the graph holds.
            projectCount = build?.ProjectCount,
            projects = projects.Count > 0 ? projects : null,
            // Where the build's time went, in the order the phases ran. A first build
            // of a large solution is slow, and this says which part was slow.
            totalSeconds = build is null ? (double?)null : Math.Round(build.Phases.Sum(p => p.Seconds), 3),
            phases = build?.Phases,
            nodeCounts,
            edgeCounts,
            // Present only on build responses that dropped vendor assets: a
            // silent skip would read as "everything was graphed".
            skippedVendorAssets = vendorSkips is { Count: > 0 } ? vendorSkips : null,
            // Same contract for excluded test projects: this graph cannot
            // answer coverage questions, and its summary must say why.
            skippedTestProjects = testProjectSkips is { Count: > 0 } ? testProjectSkips : null,
            // And for the literal index. Absent when literals were indexed, so an
            // empty find_literals against a graph that carries this flag is the
            // build's choice rather than a codebase with no strings in it -- which
            // is exactly the absence-as-finding trap the other two skips avoid.
            literalsExcluded = literalsExcluded ? true : (bool?)null,
            // And for the text-file index, for the same reason: a graph built without
            // it holds no TextFile node, and grep's graph section then has nothing to
            // say about a .json or a .md that only the build's choice explains.
            textExcluded = textExcluded ? true : (bool?)null,
            // What the text-file index left out when it ran: binaries, files over
            // 2 MB, vendor and minified text. A file absent without a word reads as
            // a file with nothing in it.
            skippedTextFiles = textFileSkips is { Count: > 0 } ? textFileSkips : null,
            // Shape names nothing binds. OrchardCore throws when a shape resolves
            // to no binding, so each is a runtime failure on whatever path
            // produces it -- a finding, and findings ride the build response
            // rather than waiting to be queried for.
            unboundShapes = unboundShapes is { Count: > 0 } ? unboundShapes : null,
            // Attribute types C# could not bind. It resolves them at compile time,
            // so this is only ever non-empty when the compilation had errors --
            // which devalues every answer in the graph, not only these edges.
            unresolvedAttributes = unresolvedAttributes is { Count: > 0 } ? unresolvedAttributes : null,
            // The SDK's source generators did not all run, so types they would
            // have produced -- Razor page classes above all -- are absent. It
            // reports here because nothing else reports it at all: a generator
            // that fails to load returns an empty list rather than throwing.
            analyzerHostWarnings = analyzerHostWarnings is { Count: > 0 } ? analyzerHostWarnings : null
        });
    }
}
