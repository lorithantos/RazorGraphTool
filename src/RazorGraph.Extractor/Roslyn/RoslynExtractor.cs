namespace RazorGraph.Extractor.Roslyn;

using RazorGraph.Extractor.Binding;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;

/// <summary>
/// The Roslyn workspace session: loads projects and solutions, holds their
/// compilations, and serves extraction passes over them. The extraction logic
/// itself lives in the family classes beside this one — SymbolClassifier,
/// CallSiteScanner, MemberAccessScanner, ExceptionFlow, MethodRoles,
/// GeneratedCodeMap, SymbolIds — this class owns what they cannot: which
/// compilations exist and when they are released.
/// </summary>
public sealed class RoslynExtractor : IAsyncDisposable
{
    private MSBuildWorkspace? _workspace;
    private readonly List<LoadedProject> _loaded = new();

    // Must run before any Microsoft.Build type is JITed; the ctor body is safe
    // because MSBuild types are first referenced inside LoadProjectAsync.
    public RoslynExtractor() => EnsureMsBuildRegistered();

    /// <summary>
    /// Register MSBuild once, whoever asks first.
    ///
    /// The check and the registration must be atomic. Unguarded, two threads
    /// both see IsRegistered false, both call RegisterDefaults, and the second
    /// throws "MSBuild assemblies were already loaded" -- because the first
    /// call's registration loaded them. Classic check-then-act.
    ///
    /// It is not a test-only concern, which is why the fix is here rather than
    /// in a fixture: the MCP server holds several graphs and can be asked to
    /// build two at once, and that is the same race with a worse failure mode --
    /// one build dies with a message about assembly loading that says nothing
    /// about the graph the caller asked for. Found by the test suite, which
    /// reproduced it once in three runs before growing enough parallel classes
    /// to hit it every time.
    ///
    /// Lazy rather than a lock, because run-exactly-once is what Lazy IS: the
    /// guarantee lives in the type instead of in a comment, and every call after
    /// the first is a volatile read rather than a lock acquisition. It also
    /// caches a failure and rethrows the same exception, which is right here --
    /// no MSBuild on the machine will not become MSBuild on the machine, and a
    /// second attempt would only produce a differently-worded failure.
    ///
    /// The one thing it gives up: this memoises OUR completion, where the lock
    /// re-read MSBuildLocator's state every call. Only an external
    /// MSBuildLocator.Unregister() could tell them apart, and nothing in this
    /// solution calls it -- the sole reference to the type is the line below.
    /// </summary>
    public static void EnsureMsBuildRegistered() => _ = Registration.Value;

    private static readonly Lazy<bool> Registration = new(() =>
    {
        // Still checked: the host application may have registered before us.
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
        return true;
    });

    /// <summary>One compiled project and the Roslyn project it came from.</summary>
    public sealed record LoadedProject(Project Project, Compilation Compilation)
    {
        public string Name => Project.Name;
        public string? FilePath => Project.FilePath;
    }

    /// <summary>
    /// Every project loaded by the most recent call. One entry for a project or
    /// single-project solution load; all of them after LoadAllProjectsAsync.
    /// </summary>
    public IReadOnlyList<LoadedProject> LoadedProjects => _loaded;

    /// <summary>The solution, when one was opened. Null for a bare project load.</summary>
    public Solution? Solution { get; private set; }

    /// <summary>
    /// The compilation from the most recent load, for consumers that need
    /// symbol-level analysis (e.g., tag helper discovery).
    /// </summary>
    public Compilation? Compilation => _loaded.Count > 0 ? _loaded[0].Compilation : null;

    /// <summary>File path of the loaded project, for locating its Razor files.</summary>
    public string? ProjectFilePath => _loaded.Count > 0 ? _loaded[0].FilePath : null;

    public async Task<Compilation> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        var project = await _workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectPath}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        return compilation;
    }

    public async Task<Compilation> LoadSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var project = Solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found in solution.");
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectName}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        return compilation;
    }

    /// <summary>
    /// Load every project in the solution. This is what makes a cross-project
    /// edge possible at all: call resolution is scoped to the assemblies that
    /// were compiled, so a graph built one project at a time can never contain
    /// an edge from a test to the code it tests.
    /// </summary>
    public async Task<IReadOnlyList<LoadedProject>> LoadAllProjectsAsync(
        string solutionPath, bool excludeTestProjects = false, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        _loaded.Clear();
        _skippedTestProjects.Clear();
        foreach (var project in Solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // The test decision is made from metadata references, before the
            // compile — skipping the compile is the point of excluding. Skips
            // are recorded and reported, never silent: a graph missing its
            // tests must say so, or every coverage query against it lies.
            if (excludeTestProjects && IsTestProject(project))
            {
                _skippedTestProjects.Add(project.Name);
                Console.Error.WriteLine($"Info: skipped test project '{project.Name}' (tests excluded).");
                continue;
            }

            // A project that will not compile is reported and skipped rather than
            // failing the whole solution: a partial graph beats no graph, and the
            // omission is visible in the project list.
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                Console.Error.WriteLine($"Warning: no compilation for project '{project.Name}'; skipped.");
                continue;
            }

            _loaded.Add(new LoadedProject(project, compilation));
        }

        if (_loaded.Count == 0)
            throw new InvalidOperationException($"No project in '{solutionPath}' produced a compilation.");

        return _loaded;
    }

    /// <summary>Test projects skipped by the most recent load, empty unless exclusion was asked for.</summary>
    public IReadOnlyList<string> SkippedTestProjects => _skippedTestProjects;
    private readonly List<string> _skippedTestProjects = new();

    /// <summary>
    /// A test project is one referencing a test framework. Decided from
    /// metadata reference file names so no compilation is needed — the same
    /// name-based membership reasoning SymbolIds.InScopeMethodId documents.
    /// Production code does not reference xunit/NUnit/MSTest, so a false
    /// positive here would be a project already lying about what it is.
    /// </summary>
    private static bool IsTestProject(Project project) =>
        project.MetadataReferences
            .OfType<PortableExecutableReference>()
            .Select(r => Path.GetFileNameWithoutExtension(r.FilePath ?? ""))
            .Any(name =>
                name.StartsWith("xunit", StringComparison.OrdinalIgnoreCase)
                || name.Equals("nunit.framework", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Extract all relevant symbols: PageModels, Controllers, Services, ViewModels.
    /// </summary>
    public IEnumerable<SymbolInfo> ExtractSymbols()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();

        var compiledItemsByLoaded = new Dictionary<LoadedProject, Dictionary<INamedTypeSymbol, string>>();

        foreach (var (loaded, tree) in _loaded.SelectMany(l => l.Compilation.SyntaxTrees.Select(t => (l, t))))
        {
            var model = loaded.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var info = SymbolClassifier.ClassifySymbol(symbol, loaded.Name, loaded.Compilation, inScope);
                if (info == null) continue;

                // A type still living in a .g.cs after mapping is generated
                // scaffolding; remember which source file it was compiled
                // from, so the builder can wire the page to its class.
                if (info.FilePath?.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!compiledItemsByLoaded.TryGetValue(loaded, out var items))
                        compiledItemsByLoaded[loaded] = items = GeneratedCodeMap.RazorCompiledItems(loaded.Compilation);
                    info.GeneratedFrom = GeneratedCodeMap.GeneratedSourcePath(symbol, items, loaded.FilePath, tree);
                }

                yield return info;
            }
        }
    }

    /// <summary>
    /// Resolve call sites to (caller, callee) method-id pairs with the
    /// exception guards enclosing each site. Only calls whose target is
    /// declared in one of the loaded projects are returned -- an edge to
    /// String.Format would be noise, not navigation.
    ///
    /// Membership is tested by assembly *name*, not symbol identity. A call into
    /// a sibling project may bind to either a source symbol or a metadata symbol
    /// depending on how the workspace resolved the reference, and those are not
    /// reference-equal; the name is the same either way, and so is the MethodId
    /// the node was registered under.
    ///
    /// Besides explicit invocations, two compiler-emitted calls carry edges: a
    /// new-expression is a call to the constructor, and a using/await using is a
    /// call to the resource's Dispose/DisposeAsync. Neither has invocation
    /// syntax at the site, and without the edges the methods read as unreached
    /// by the very code that guarantees they run.
    /// </summary>
    public IEnumerable<CallSiteInfo> ExtractCallSites()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var site in root.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>()
                .SelectMany(decl => CallSiteScanner.MethodCallSites(decl, model, inScope)))
            {
                yield return site;
            }

            foreach (var site in root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .SelectMany(typeDecl => CallSiteScanner.InitializerCallSites(typeDecl, model, inScope)))
            {
                yield return site;
            }
        }
    }

    /// <summary>
    /// The views each controller action renders, with the name worked out from
    /// the semantic model; see ViewCallScanner. Names are not resolved to files
    /// here — that needs the Razor layer, which is built later.
    /// </summary>
    public IEnumerable<ViewCall> ExtractViewCalls()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var call in root.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>()
                .SelectMany(decl => ViewCallScanner.ViewCalls(decl, model)))
            {
                yield return call;
            }
        }
    }

    /// <summary>
    /// Shape names produced by display drivers and shape factories; see
    /// ViewCallScanner. Empty on a solution with no such types, which is every
    /// solution that is not OrchardCore-shaped.
    /// </summary>
    public IEnumerable<ShapeReference> ExtractShapeNames()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var loaded in _loaded)
        {
            // Decided once per project, not once per shape: a test fixture naming
            // a shape is not a render path, and reporting it as one puts noise in
            // the only stream this pass exists to produce. OrchardCore's whole
            // solution surfaced exactly one such name (ContentZone, from
            // DisplayDriverTestHelper) among four.
            var inTestCode = IsTestProject(loaded.Project);

            foreach (var (root, model) in loaded.Compilation.SyntaxTrees
                         .Select(t => (t.GetRoot(), loaded.Compilation.GetSemanticModel(t))))
            {
                foreach (var shape in root.DescendantNodes()
                    .OfType<BaseMethodDeclarationSyntax>()
                    .SelectMany(decl => ViewCallScanner.ShapeNames(decl, model)))
                {
                    yield return inTestCode ? shape with { InTestCode = true } : shape;
                }
            }
        }
    }

    /// <summary>
    /// Every controller action and the name routing knows it by; see
    /// ViewCallScanner. Needed to attribute a helper's render to the action that
    /// invoked it, which is the only thing that knows the view name.
    /// </summary>
    public IEnumerable<ActionMethod> ExtractActionMethods()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var action in root.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>()
                .Select(decl => ViewCallScanner.ActionMethodOf(decl, model)))
            {
                if (action is not null) yield return action;
            }
        }
    }

    /// <summary>
    /// Every read and write of an in-solution property or field, attributed to
    /// the code that performs it; see MemberAccessScanner.
    /// </summary>
    public IEnumerable<MemberAccessInfo> ExtractMemberAccesses()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var access in MemberAccessScanner.TreeMemberAccesses(root, model, inScope))
                yield return access;
        }
    }

    /// <summary>
    /// Methods that out-of-solution code can call back into; see
    /// CallSiteScanner.CallbackTargets.
    /// </summary>
    public IEnumerable<string> ExtractCallbackTargets()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                foreach (var targetId in CallSiteScanner.CallbackTargets(decl, model, inScope))
                    yield return targetId;
            }
        }
    }

    /// <summary>
    /// The control-flow graph of one method's body, located by the same id its
    /// Method node is registered under. Null when the id matches nothing or the
    /// method has no body. Requires a loaded project — body graphs are computed
    /// from the live compilation, not from a serialized graph.
    /// </summary>
    public BodyGraph? GetMethodBodyGraph(string methodId)
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var loaded in _loaded)
        {
            foreach (var tree in loaded.Compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    model ??= loaded.Compilation.GetSemanticModel(tree);
                    if (model.GetDeclaredSymbol(decl) is not IMethodSymbol symbol) continue;
                    if (SymbolIds.MethodId(symbol) != methodId) continue;

                    return BodyGraphExtractor.Extract(model, decl, SymbolIds.MethodId);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Assembly names of the loaded projects — the membership test every
    /// extraction pass scopes itself with.
    /// </summary>
    private HashSet<string> InScopeAssemblies() =>
        _loaded
            .Select(l => l.Compilation.Assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

    public async ValueTask DisposeAsync()
    {
        if (_workspace != null)
        {
            _workspace.CloseSolution();
            _workspace.Dispose();
        }
    }
}
