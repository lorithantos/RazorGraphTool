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
    private readonly CodeGraph _graph = new();
    private readonly RoslynExtractor _roslyn = new();

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
    private readonly ProjectEmitter _projects;
    private readonly CoverageEmitter _coverage;

    public GraphBuilder()
    {
        _declarations = new DeclarationEmitter(_graph);
        _usage = new UsageEmitter(_graph);
        _escapes = new EscapeEmitter(_graph);
        _clientAssets = new ClientAssetEmitter(_graph);
        _razorLayer = new RazorLayerEmitter(_graph, _clientAssets);
        _projects = new ProjectEmitter(_graph);
        _coverage = new CoverageEmitter(_graph);
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
    /// Skip test projects when building from a solution — no test Method
    /// nodes, no Covers edges, roughly a fifth of the edges on a well-tested
    /// solution. Off by default: the default graph must be able to answer
    /// every question, and a coverage query against a test-less graph would
    /// report everything uncovered. Skipped projects are always reported.
    /// </summary>
    public bool ExcludeTestProjects { get; set; }

    /// <summary>Test projects the solution load skipped; empty unless ExcludeTestProjects.</summary>
    public IReadOnlyList<string> SkippedTestProjects => _roslyn.SkippedTestProjects;

    public async Task<CodeGraph> BuildFromProjectAsync(string projectPath, CancellationToken ct = default)
    {
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectPath))
            ?? throw new ArgumentException("Invalid project path", nameof(projectPath));

        await _roslyn.LoadProjectAsync(projectPath, ct);
        return BuildGraph(projectDir);
    }

    public async Task<CodeGraph> BuildFromSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        await _roslyn.LoadSolutionAsync(solutionPath, projectName, ct);

        var projectFile = _roslyn.ProjectFilePath
            ?? throw new InvalidOperationException($"Project '{projectName}' has no file path.");
        var projectDir = Path.GetDirectoryName(Path.GetFullPath(projectFile))
            ?? throw new InvalidOperationException($"Invalid project file path: {projectFile}");

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
        await _roslyn.LoadAllProjectsAsync(solutionPath, ExcludeTestProjects, ct);
        return BuildSolutionGraph();
    }

    private CodeGraph BuildGraph(string projectDir)
    {
        BuildRoslynLayer();
        _razorLayer.BuildRazorLayer(projectDir, idScope: null, _roslyn.Compilation, _symbols, IncludeVendorAssets);
        _razorLayer.AddGeneratedClassLinks(_symbols);
        return _graph;
    }

    private CodeGraph BuildSolutionGraph()
    {
        BuildRoslynLayer();

        foreach (var loaded in _roslyn.LoadedProjects)
        {
            if (loaded.FilePath == null) continue;
            var projectDir = Path.GetDirectoryName(Path.GetFullPath(loaded.FilePath));
            if (projectDir == null) continue;

            _razorLayer.BuildRazorLayer(projectDir, loaded.Name, loaded.Compilation, _symbols, IncludeVendorAssets);
        }

        _razorLayer.AddGeneratedClassLinks(_symbols);
        _projects.AddProjectNodes(_roslyn.LoadedProjects, _roslyn.Solution);
        _coverage.AddCoverageEdges();
        return _graph;
    }

    /// <summary>
    /// Phases 1-4b: everything Roslyn knows, across every loaded project. Symbols
    /// from all projects are added before any call edge, because a call edge whose
    /// target has not been registered yet is silently dropped.
    /// </summary>
    private void BuildRoslynLayer()
    {
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
            _declarations.AddMemberTypeReferences(sym);
        }

        _declarations.AddExtensionEdges(_symbols);
        _declarations.AddMethodImplementsEdges(_symbols);

        foreach (var sym in symbols)
        {
            _declarations.AddInjectionEdges(sym);
        }

        _usage.AddCallEdges(_roslyn.ExtractCallSites());
        _usage.AddMemberAccessEdges(_roslyn.ExtractMemberAccesses());
        _usage.AddCallbackEntryPoints(_roslyn.ExtractCallbackTargets());
        _escapes.AddExceptionEscapeEdges(_declarations.MethodThrows);
    }

    public ValueTask DisposeAsync() => _roslyn.DisposeAsync();
}
