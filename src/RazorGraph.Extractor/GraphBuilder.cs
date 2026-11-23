namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Roslyn;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// Orchestrates Roslyn + Razor extraction into a unified CodeGraph. The
/// emitting itself lives in the emitter classes beside this one — declarations,
/// usage, escapes, the Razor layer, client assets, projects, coverage — each
/// holding the shared graph sink and its own working registries. This class
/// owns the build order, the extractor session, and the accumulated symbol
/// list the passes share.
/// </summary>
public sealed class GraphBuilder : IAsyncDisposable
{
    // Stamped at construction rather than at return: extraction is the slow part,
    // and the baseline a consumer wants is the source as it stood when reading
    // began. Stamping afterwards would call a file edited mid-build fresh.
    private readonly CodeGraph _graph = new()
    {
        BuiltAt = DateTimeOffset.UtcNow,
        ExtractorBuiltAt = ExtractorBuildTime(),
        HostArchitecture = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()
    };
    private readonly RoslynExtractor _roslyn = new();

    internal const string PlatformRequested = "requested";
    internal const string PlatformFromEnvironment = "environment";
    internal const string PlatformProjectDefault = "projectDefault";

    /// <summary>
    /// What the workspace's Platform will be and why, by MSBuild's own order: a
    /// global property wins, then an environment variable of that name, then the
    /// project's default. The environment counts because MSBuild reads it, and the
    /// out-of-proc build host MSBuildWorkspace evaluates in inherits ours.
    /// </summary>
    /// <remarks>
    /// Pure, and the inputs are arguments, so the rule can be tested without
    /// touching a process-wide variable the parallel test run shares.
    /// </remarks>
    internal static (string? Platform, string From) ResolvePlatform(string? requested, string? environment)
    {
        if (requested is not null) return (requested, PlatformRequested);
        if (!string.IsNullOrEmpty(environment)) return (environment, PlatformFromEnvironment);
        return (null, PlatformProjectDefault);
    }

    /// <summary>
    /// Record the Platform this build loaded with. Called from every build entry
    /// point beside Source, because a graph that does not say this cannot explain
    /// why its generated-code paths differ from another build of the same source.
    /// </summary>
    private void StampPlatform()
    {
        (_graph.Platform, _graph.PlatformFrom) =
            ResolvePlatform(_roslyn.Platform, Environment.GetEnvironmentVariable("Platform"));
    }

    /// <summary>
    /// Record which projects the load found unrestored, beside Platform and for
    /// the same reason: the graph's answers depend on it, and a coverage refusal
    /// long after the build can only name the cause if the graph carries it.
    /// </summary>
    private void StampRestore() => _graph.UnrestoredProjects = _roslyn.UnrestoredProjects.ToList();

    /// <summary>
    /// Last-write time of the assembly this extractor lives in -- the closest
    /// thing to "which build produced this graph" that does not require knowing
    /// how the host deploys itself. Null rather than a guess when the location is
    /// unavailable, which a single-file publish reports as an empty string.
    /// </summary>
    private static DateTimeOffset? ExtractorBuildTime()
    {
        try
        {
            var location = typeof(GraphBuilder).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !File.Exists(location)) return null;
            return new DateTimeOffset(File.GetLastWriteTimeUtc(location), TimeSpan.Zero);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Symbols from every loaded project. Razor correlation runs per project but
    /// must resolve against the whole solution: a page's @model can name a type
    /// declared in a class library.
    /// </summary>
    private readonly List<SymbolInfo> _symbols = new();

    private readonly DeclarationEmitter _declarations;
    private readonly UsageEmitter _usage;
    private readonly EscapeEmitter _escapes;
    private readonly ClientAssetEmitter _clientAssets;
    private readonly RazorLayerEmitter _razorLayer;
    private readonly XamlLayerEmitter _xamlLayer;
    private readonly ProjectEmitter _projects;
    private readonly CoverageEmitter _coverage;
    private readonly TextFileEmitter _textFiles;

    public GraphBuilder()
    {
        _declarations = new DeclarationEmitter(_graph);
        _usage = new UsageEmitter(_graph);
        _escapes = new EscapeEmitter(_graph);
        _clientAssets = new ClientAssetEmitter(_graph);
        _razorLayer = new RazorLayerEmitter(_graph, _clientAssets);
        _xamlLayer = new XamlLayerEmitter(_graph);
        _projects = new ProjectEmitter(_graph);
        _coverage = new CoverageEmitter(_graph);
        _textFiles = new TextFileEmitter(_graph);
    }

    /// <summary>
    /// Graph vendor and minified client assets instead of dropping them. Vendor
    /// detection always runs; this switches the policy from drop to keep, for
    /// when the bug being hunted lives inside a shipped bundle. Included vendor
    /// nodes carry vendor=true and a vendorReason so queries can still tell the
    /// tiers apart.
    /// </summary>
    public bool IncludeVendorAssets { get; set; }

    /// <summary>One line per project whose client-asset scan dropped vendor files.</summary>
    public IReadOnlyList<string> AssetSkipSummaries => _clientAssets.AssetSkipSummaries;

    /// <summary>
    /// Shape names nothing binds. Each is a runtime InvalidOperationException on
    /// whatever path produces it, and must reach every build report the same way
    /// vendor skips do.
    /// </summary>
    public IReadOnlyList<string> UnboundShapes => _razorLayer.UnboundShapes;

    /// <summary>
    /// Attribute sites whose class did not bind. C# resolves attribute types at
    /// compile time, so a non-empty list means the compilation had errors — a
    /// finding about the build, and one that devalues every other answer the
    /// graph gives. Reported for the same reason unbound shapes are.
    /// </summary>
    public IReadOnlyList<string> UnresolvedAttributes => _declarations.UnresolvedAttributes;

    /// <summary>
    /// Skip test projects when building from a solution — no test Method
    /// nodes, no Covers edges, roughly a fifth of the edges on a well-tested
    /// solution. Off by default: the default graph must be able to answer
    /// every question, and a coverage query against a test-less graph would
    /// report everything uncovered. Skipped projects are always reported.
    /// </summary>
    public bool ExcludeTestProjects { get; set; }

    /// <summary>Test projects the solution load skipped; empty unless ExcludeTestProjects.</summary>
    public IReadOnlyList<string> SkippedTestProjects => _roslyn.SkippedTestProjects;

    /// <summary>
    /// Skip the literal index -- no Literal nodes, no Mentions edges. Off by
    /// default for the same reason ExcludeTestProjects is: the default graph must
    /// be able to answer every question, and a graph built without literals
    /// answers "where is this string" with nothing, which reads as "nowhere".
    /// Quotes edges are unaffected either way; they are a different question over
    /// the same scan.
    /// </summary>
    public bool ExcludeLiterals { get; set; }

    /// <summary>
    /// Skip the TextFile population -- no node for any non-C# text file, so grep's
    /// graph section answers for C# names and literals alone and every other line comes
    /// from the disk. Off by default for the reason ExcludeLiterals is.
    /// </summary>
    public bool ExcludeText { get; set; }

    /// <summary>
    /// What the TextFile population left out -- binaries, files over 2 MB, unreadable
    /// files, vendor and minified text -- one line per reason. Empty when nothing was
    /// skipped, and always empty under ExcludeText, which skips them all and says so.
    /// </summary>
    public IReadOnlyList<string> TextFileSkips => _textFiles.SkipSummaries;

    /// <summary>
    /// The MSBuild Platform to build for, passed as a global property; null, the
    /// default, passes none and inherits whatever the environment says. Either
    /// way the graph records the value used and where it came from.
    /// </summary>
    public string? Platform
    {
        get => _roslyn.Platform;
        set => _roslyn.Platform = value;
    }

    /// <summary>
    /// Reasons the SDK's source generators may not have run for this build. A
    /// generator that fails to load takes its generated types with it in
    /// silence, so this list is the difference between a graph that is missing
    /// Razor page classes and a graph that says it is; see AnalyzerHostCheck.
    /// </summary>
    public IReadOnlyList<string> AnalyzerHostWarnings => _roslyn.AnalyzerHostWarnings;

    /// <summary>
    /// Told as each phase starts, and as each project starts inside the phases
    /// that walk the solution a project at a time. Reports arrive synchronously on
    /// the build's thread, in order. Null, the default, tells no one; the phases
    /// are timed either way.
    /// </summary>
    public IProgress<BuildProgress>? Progress { get; set; }

    private BuildClock? _clock;

    /// <summary>How long each phase of the last build took, in the order they ran. Empty before a build.</summary>
    public IReadOnlyList<BuildPhaseTiming> PhaseTimings => _clock?.Timings ?? [];

    /// <summary>
    /// Projects in the solution (or the one project) the last build was asked to
    /// graph -- the M of "project N of M", counting test projects a --no-tests
    /// build skipped.
    /// </summary>
    public int ProjectCount { get; private set; }

    /// <summary>
    /// Projects that did not load or did not compile cleanly, root failures first,
    /// each named with its first few errors as file:line. The graph is still built
    /// from what did load; this is what makes that partial graph say it is one.
    /// </summary>
    public IReadOnlyList<string> ProjectFailures => _roslyn.ProjectFailures;

    private void StartClock(IReadOnlyList<string> plan)
    {
        _clock = new BuildClock(plan, Progress);
        _roslyn.Clock = _clock;
    }

    /// <summary>
    /// The attribute policy for this build — classification name sets and
    /// argument-payload suppression as data. Defaults to the embedded shipped
    /// policy; set it from AttributePolicy.LoadFile to override without a
    /// rebuild, which is the property that makes it configuration.
    /// </summary>
    public Attributes.AttributePolicy AttributePolicy
    {
        get => _roslyn.Policy;
        set
        {
            _roslyn.Policy = value;
            _declarations.Policy = value;
        }
    }

    public async Task<CodeGraph> BuildFromProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? throw new ArgumentException("Invalid project path", nameof(projectPath));

        StartClock(BuildPhases.Project);
        await _roslyn.LoadProjectAsync(projectPath, ct);
        ProjectCount = 1;
        _graph.Source = Path.GetFullPath(projectPath);
        StampPlatform();
        StampRestore();
        return BuildGraph(projectDir);
    }

    public async Task<CodeGraph> BuildFromSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        StartClock(BuildPhases.Project);
        await _roslyn.LoadSolutionAsync(solutionPath, projectName, ct);
        ProjectCount = 1;

        var projectFile = _roslyn.ProjectFilePath
            ?? throw new InvalidOperationException($"Project '{projectName}' has no file path.");
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))
            ?? throw new InvalidOperationException($"Invalid project file path: {projectFile}");

        // The solution, not the project inside it: that is the path a caller
        // rebuilds from, and the one build_graph has always reported.
        _graph.Source = Path.GetFullPath(solutionPath);
        StampPlatform();
        StampRestore();
        return BuildGraph(projectDir);
    }

    /// <summary>
    /// Build one graph spanning every project in the solution.
    ///
    /// This is not the per-project build run in a loop. Call resolution is scoped
    /// to the set of compiled assemblies, so edges that leave a project -- a test
    /// exercising a service, a page calling into a class library -- exist only if
    /// both ends were compiled together. Node ids for file-relative concepts are
    /// qualified by project name to keep them unique.
    /// </summary>
    public async Task<CodeGraph> BuildFromSolutionAllAsync(string solutionPath, CancellationToken ct = default)
    {
        StartClock(BuildPhases.Solution);
        await _roslyn.LoadAllProjectsAsync(solutionPath, ExcludeTestProjects, ct);
        ProjectCount = _roslyn.Solution?.Projects.Count() ?? _roslyn.LoadedProjects.Count;
        _graph.Source = Path.GetFullPath(solutionPath);
        StampPlatform();
        StampRestore();
        return BuildSolutionGraph();
    }

    private CodeGraph BuildGraph(string projectDir)
    {
        BuildRoslynLayer();
        _clock?.Begin(BuildPhases.Razor);
        _clock?.Project(1, 1, _roslyn.LoadedProjects[0].Name);
        _razorLayer.BuildRazorLayer(projectDir, idScope: null, _roslyn.Compilation, _symbols, IncludeVendorAssets);
        _xamlLayer.AddXamlLayer(projectDir, idScope: null);
        _clock?.Begin(BuildPhases.Links);
        _razorLayer.AddGeneratedClassLinks(_symbols);
        _razorLayer.AddInjectDirectiveEdges();
        var viewSites = _roslyn.ExtractViewBindingSites();
        _razorLayer.AddBindingEdges(viewSites.ViewCalls, viewSites.ShapeNames, viewSites.ActionMethods);
        // A single-project graph, even one loaded from a solution, is rooted at the
        // project: the rest of the solution's text is not this graph's to hold.
        _clock?.Begin(BuildPhases.TextFiles);
        AddTextFiles(projectDir);
        _clock?.Finish();
        return _graph;
    }

    /// <summary>
    /// The TextFile population, last: it leaves out every file the graph already holds
    /// as a JavaScriptFile or CssFile, so the client tier must exist first.
    /// </summary>
    private void AddTextFiles(string sourceRoot)
    {
        if (ExcludeText) return;

        var alreadyFiles = new HashSet<string>(_clientAssets.SkippedAssetPaths, StringComparer.OrdinalIgnoreCase);
        foreach (var node in _graph.Nodes)
        {
            if ((node.Type is NodeType.JavaScriptFile or NodeType.CssFile) && node.FilePath is { } path)
                alreadyFiles.Add(Path.GetFullPath(path));
        }

        _textFiles.AddTextFiles(sourceRoot, alreadyFiles);
    }

    private CodeGraph BuildSolutionGraph()
    {
        BuildRoslynLayer();

        _clock?.Begin(BuildPhases.Razor);
        var loadedProjects = _roslyn.LoadedProjects;
        for (var i = 0; i < loadedProjects.Count; i++)
        {
            var loaded = loadedProjects[i];
            _clock?.Project(i + 1, loadedProjects.Count, loaded.Name);
            if (loaded.FilePath == null) continue;
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(loaded.FilePath));
            if (projectDir == null) continue;

            _razorLayer.BuildRazorLayer(projectDir, loaded.Name, loaded.Compilation, _symbols, IncludeVendorAssets);
            _xamlLayer.AddXamlLayer(projectDir, loaded.Name);
        }

        _clock?.Begin(BuildPhases.Links);
        _razorLayer.AddGeneratedClassLinks(_symbols);
        _razorLayer.AddInjectDirectiveEdges();

        // Projects before bindings: a view name resolves against the referencing
        // project and the ones it references, which needs the project nodes and
        // their DependsOn edges to already exist.
        _projects.AddProjectNodes(_roslyn.LoadedProjects, _roslyn.Solution);

        // After the Project nodes exist: assembly/module attributes hang off
        // proj:, and the shared HasNode guard would drop them into silence if
        // this ran a line earlier.
        foreach (var (projectName, attributes) in _roslyn.ExtractAssemblyAttributes())
            _declarations.AddProjectAttributeEdges(ProjectEmitter.ProjectId(projectName), attributes);

        var viewSites = _roslyn.ExtractViewBindingSites();
        _razorLayer.AddBindingEdges(viewSites.ViewCalls, viewSites.ShapeNames, viewSites.ActionMethods);

        _clock?.Begin(BuildPhases.Coverage);
        _coverage.AddCoverageEdges();
        _clock?.Begin(BuildPhases.TextFiles);
        AddTextFiles(Path.GetDirectoryName(_graph.Source!)!);
        _clock?.Finish();
        return _graph;
    }

    /// <summary>
    /// Phases 1-4b: everything Roslyn knows, across every loaded project. Symbols
    /// from all projects are added before any call edge, because a call edge whose
    /// target has not been registered yet is silently dropped.
    /// </summary>
    private void BuildRoslynLayer()
    {
        _clock?.Begin(BuildPhases.Symbols);
        var symbols = _roslyn.ExtractSymbols().ToList();
        _symbols.AddRange(symbols);

        foreach (var sym in symbols)
        {
            _declarations.AddSymbolNode(sym);
            _declarations.AddMethodNodes(sym);
            _declarations.AddMemberNodes(sym);
        }

        foreach (var sym in symbols)
        {
            _declarations.AddInheritanceEdges(sym);
            _declarations.AddDeclaredTypeReferences(sym);
        }

        _declarations.AddExtensionEdges(_symbols);
        _declarations.AddTypeImplementsEdges(_symbols);
        _declarations.AddMethodImplementsEdges(_symbols);
        _declarations.AddMemberImplementsEdges(_symbols);
        _declarations.AddMethodOverridesEdges(_symbols);

        foreach (var sym in symbols)
        {
            _declarations.AddInjectionEdges(sym);
            _declarations.AddAttributeEdges(sym);
        }

        _clock?.Begin(BuildPhases.Calls);
        var usageSites = _roslyn.ExtractUsageSites();
        _usage.AddCallEdges(usageSites.Calls, _declarations.TypeIdFor);
        _declarations.AddConstructsEdges(usageSites.Constructions);
        _usage.AddMemberAccessEdges(usageSites.MemberAccesses);
        // One scan, two consumers. The quoted names come from the same walk as
        // the calls, and the one list is handed to both.
        //
        // The two do genuinely different things with it. AddQuotesEdges keeps only
        // strings equal to a uniquely-declared name -- rename safety.
        // AddMentionsEdges keeps all of them, deduplicated by value -- findability.
        // Folding either into the other would make one of the two answers wrong.
        //
        // After every declaration is a node: the name index a quoted string is
        // matched against is built from the graph, so anything emitted later
        // would be invisible to it.
        var quotedNames = usageSites.QuotedNames;
        _usage.AddQuotesEdges(quotedNames);
        // Second, and never first: AddQuotesEdges reads the graph's own names to
        // build its uniqueness index, and Literal nodes are named after their
        // values. Nameable() excludes them so the order is not what makes this
        // safe, but there is no reason to make the order argue with the guard.
        if (!ExcludeLiterals) _usage.AddMentionsEdges(quotedNames);
        _usage.AddCallbackEntryPoints(usageSites.CallbackTargets);
        _clock?.Begin(BuildPhases.Escapes);
        _escapes.AddExceptionEscapeEdges(_declarations.MethodThrows);
    }

    public ValueTask DisposeAsync() => _roslyn.DisposeAsync();
}
