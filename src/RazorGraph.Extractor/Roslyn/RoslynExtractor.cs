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

    /// <summary>
    /// The attribute policy every classification pass consults — the embedded
    /// default unless the build was pointed at an override file. Set before
    /// extraction starts; per-instance rather than static so concurrent builds
    /// cannot see each other's policy.
    /// </summary>
    public Attributes.AttributePolicy Policy { get; set; } = Attributes.AttributePolicy.Default;

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

    /// <summary>
    /// The located SDK, kept rather than discarded because its MSBuildPath is
    /// how AnalyzerHostCheck finds the Roslyn that SDK ships. Null when the host
    /// registered before us and no instance can be queried back -- "cannot
    /// tell", which the check treats as silence rather than as compatible.
    /// </summary>
    private static readonly Lazy<VisualStudioInstance?> Registration = new(() =>
    {
        // Still checked: the host application may have registered before us.
        if (MSBuildLocator.IsRegistered) return MSBuildLocator.QueryVisualStudioInstances().FirstOrDefault();
        return MSBuildLocator.RegisterDefaults();
    });

    /// <summary>
    /// Directory of the SDK MSBuildLocator resolved, or null when it cannot be
    /// determined. The Roslyn that SDK ships lives beneath it; see
    /// AnalyzerHostCheck.SdkRoslynVersion.
    /// </summary>
    internal static string? SdkPath => Registration.Value?.MSBuildPath;

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

    /// <summary>
    /// The MSBuild <c>Platform</c> to load with, passed to the workspace as a
    /// global property -- x64, AnyCPU, ARM64. Null, the default, passes none, so
    /// MSBuild takes Platform from this process's environment if it is set there
    /// and from each project's default if not. Blank is null: an empty global
    /// property would not mean "unset", it would override the environment with
    /// nothing.
    /// </summary>
    public string? Platform
    {
        get => _platform;
        set => _platform = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
    private string? _platform;

    /// <summary>
    /// The workspace every load opens. Without a requested Platform this is the
    /// parameterless Create it has always been, so a build that asks for nothing
    /// behaves exactly as before; the graph only records what it inherited.
    /// </summary>
    private MSBuildWorkspace CreateWorkspace() => Platform is { } platform
        ? MSBuildWorkspace.Create(new Dictionary<string, string> { ["Platform"] = platform })
        : MSBuildWorkspace.Create();

    /// <summary>
    /// Where this session's phases are timed and announced. Set by GraphBuilder
    /// before a load; null, as for a bare extractor, times and announces nothing.
    /// </summary>
    internal BuildClock? Clock { get; set; }

    public async Task<Compilation> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        Clock?.Begin(BuildPhases.LoadProject);
        _workspace = CreateWorkspace();
        var project = await _workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        RecordLoadFailures(project.Solution);
        RecordUnrestored(project.Solution, projectPath);

        Clock?.Begin(BuildPhases.Compile);
        Clock?.Project(1, 1, project.Name);
        WatchAnalyzerHost(project);
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectPath}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        RecordCompileErrors(ct);
        return compilation;
    }

    public async Task<Compilation> LoadSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        Clock?.Begin(BuildPhases.LoadProject);
        _workspace = CreateWorkspace();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        RecordLoadFailures(Solution);
        RecordUnrestored(Solution, solutionPath);
        var project = Solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found in solution.");

        Clock?.Begin(BuildPhases.Compile);
        Clock?.Project(1, 1, project.Name);
        WatchAnalyzerHost(project);
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectName}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        RecordCompileErrors(ct);
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
        Clock?.Begin(BuildPhases.LoadSolution);
        _workspace = CreateWorkspace();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        RecordLoadFailures(Solution);
        RecordUnrestored(Solution, solutionPath);

        Clock?.Begin(BuildPhases.Compile);
        _loaded.Clear();
        _skippedTestProjects.Clear();
        var projects = Solution.Projects.ToList();
        for (var i = 0; i < projects.Count; i++)
        {
            var project = projects[i];
            ct.ThrowIfCancellationRequested();
            Clock?.Project(i + 1, projects.Count, project.Name);

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

            WatchAnalyzerHost(project);

            // A project that will not compile is reported and skipped rather than
            // failing the whole solution: a partial graph beats no graph, and the
            // omission is visible in the project list.
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                Console.Error.WriteLine($"Warning: no compilation for project '{project.Name}'; skipped.");
                FailureFor(project, project.Name).Headers.Add("produced no compilation, so it is not in the graph");
                continue;
            }

            _loaded.Add(new LoadedProject(project, compilation));
        }

        if (_loaded.Count == 0)
            throw new InvalidOperationException($"No project in '{solutionPath}' produced a compilation.");

        RecordCompileErrors(ct);
        return _loaded;
    }

    /// <summary>
    /// Projects that did not load or did not compile cleanly: a header line per
    /// project and then its first few errors as file:line, every line prefixed with
    /// the project's name. Empty is a clean build. Partial is still a graph --
    /// what gets built is unchanged -- but a partial graph that does not say so
    /// answers "who calls this" with fewer rows and no hint that rows are missing.
    /// </summary>
    /// <remarks>
    /// Root failures come first. A project that references another failing
    /// project says so in its header, because its errors may be that project's
    /// missing types arriving downstream rather than anything wrong in its own
    /// source. That is a statement about references, which the solution knows,
    /// not a verdict on each error, which it does not.
    /// </remarks>
    internal IReadOnlyList<string> ProjectFailures => FormatFailures();

    private readonly List<ProjectFailure> _failures = new();

    /// <summary>One failing project: what went wrong, and the lines that show it.</summary>
    private sealed class ProjectFailure(Project? project, string name)
    {
        public Project? Project { get; } = project;
        public string Name { get; } = name;
        public List<string> Headers { get; } = new();
        public List<string> Details { get; } = new();
    }

    private ProjectFailure FailureFor(Project? project, string name)
    {
        var existing = _failures.FirstOrDefault(f => f.Name == name);
        if (existing != null) return existing;
        var created = new ProjectFailure(project, name);
        _failures.Add(created);
        return created;
    }

    /// <summary>How many errors to list per failing project. The count is always given in full.</summary>
    internal const int ErrorsListedPerProject = 3;

    private const string DidNotLoad = "did not load, so none of its code is in the graph";

    private List<string> FormatFailures()
    {
        var failingIds = _failures.Where(f => f.Project != null).Select(f => f.Project!.Id).ToHashSet();

        var rows = _failures.Select(f =>
        {
            var upstream = f.Project is { } p
                ? p.Solution.GetProjectDependencyGraph().GetProjectsThatThisProjectTransitivelyDependsOn(p.Id)
                    .Where(failingIds.Contains)
                    .Select(id => p.Solution.GetProject(id)?.Name ?? id.ToString())
                    .OrderBy(n => n, StringComparer.Ordinal)
                    .ToList()
                : new List<string>();
            return (Failure: f, Upstream: upstream);
        });

        var lines = new List<string>();
        // Roots first; OrderBy is stable, so within each group the build's own order holds.
        foreach (var (failure, upstream) in rows.OrderBy(r => r.Upstream.Count > 0))
        {
            var header = string.Join("; ", failure.Headers);
            if (upstream.Count > 0)
                header += $"; it references {string.Join(", ", upstream)}, which also failed, so these errors " +
                    "may follow from that rather than from its own source";
            lines.Add($"{failure.Name}: {header}.");
            lines.AddRange(failure.Details.Select(d => $"{failure.Name}: {d}"));
        }
        return lines;
    }

    /// <summary>
    /// The workspace's own load failures. MSBuildWorkspace does not throw for a
    /// project it cannot evaluate: it logs the failure here and leaves the project
    /// in the solution with no documents, so it compiles to nothing and reads as
    /// a project with no code. Matched back to a project by its file path in the
    /// message, which is where MSBuild puts it.
    /// </summary>
    /// <remarks>
    /// Failure is not proof of failure: the workspace files MSBuild warnings under
    /// it too (eShopOnWeb's NuGet vulnerability warnings, NU1902/NU1903, arrive as
    /// "Msbuild failed when processing the file" on projects that loaded whole). So
    /// a project counts as not loaded only when it also came out with no documents,
    /// which is what a real load failure leaves behind.
    /// </remarks>
    private void RecordLoadFailures(Solution solution)
    {
        if (_workspace is null) return;

        foreach (var diagnostic in _workspace.Diagnostics.Where(d => d.Kind == WorkspaceDiagnosticKind.Failure))
        {
            var owner = solution.Projects.FirstOrDefault(p =>
                p.FilePath is { } path && diagnostic.Message.Contains(path, StringComparison.OrdinalIgnoreCase));
            if (owner is { DocumentIds.Count: > 0 }) continue;
            // Unowned is a project the solution names but the workspace never made:
            // a C# project whose file is missing is a failure, while a project type
            // with no language (eShopOnWeb's docker-compose.dcproj) was never going
            // to be graphed and is skipped by design.
            if (owner is null && !diagnostic.Message.Contains(".csproj", StringComparison.OrdinalIgnoreCase)) continue;

            var failure = FailureFor(owner, owner?.Name ?? "(solution)");
            if (!failure.Headers.Contains(DidNotLoad)) failure.Headers.Add(DidNotLoad);
            // Once each: MSBuild repeats the failure for every project that references the broken one.
            if (!failure.Details.Contains(diagnostic.Message)) failure.Details.Add(diagnostic.Message);
        }
    }

    /// <summary>
    /// Projects the last load opened that had no project.assets.json -- never
    /// restored. Empty when every project was. The same projects lead their
    /// entries in <see cref="ProjectFailures"/> with the fix.
    /// </summary>
    internal IReadOnlyList<string> UnrestoredProjects => _unrestored;
    private readonly List<string> _unrestored = new();

    private const string AssetsFileName = "project.assets.json";

    /// <summary>
    /// Name every project that was never restored, folded into its project
    /// failure with the command that fixes it. RazorGraph does not restore, and
    /// an unrestored project fails quietly: the design-time build suppresses
    /// NETSDK1004, so the workspace reports nothing, and what arrives is a pile of
    /// CS0246 errors for package types -- [Fact] among them, which is how a fresh
    /// clone's graph comes to hold no test methods. The errors say what did not
    /// bind; only this says why.
    /// </summary>
    private void RecordUnrestored(Solution solution, string restoreTarget)
    {
        _unrestored.Clear();
        var fix = $"run dotnet restore \"{Path.GetFullPath(restoreTarget)}\" and rebuild";
        foreach (var project in solution.Projects)
        {
            if (project.FilePath is not { } file || !RestoreExpected(file) || AssetsFileFound(project, file)) continue;

            _unrestored.Add(project.Name);
            FailureFor(project, project.Name).Headers.Add(
                $"not restored -- it has no {AssetsFileName}, so its package references did not resolve " +
                $"and the code that uses them did not bind; {fix}");
        }
    }

    /// <summary>
    /// Whether the project restores to an assets file at all: SDK-style, or
    /// declaring a PackageReference. A packages.config project never has one,
    /// and calling it unrestored would be a false alarm on every legacy build.
    /// Unreadable is false -- cannot tell is not an alarm.
    /// </summary>
    private static bool RestoreExpected(string projectFile)
    {
        try
        {
            var text = File.ReadAllText(projectFile);
            return text.Contains("Sdk=", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<Sdk ", StringComparison.OrdinalIgnoreCase)
                || text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase);
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// Whether the project's assets file exists. obj\ beside the project is the
    /// default; a moved obj (BaseIntermediateOutputPath, the artifacts layout)
    /// is found from the editorconfig the SDK generates into IntermediateOutputPath,
    /// which sits a level or two below the folder restore writes to -- walked up
    /// a few levels, and never past a folder named obj.
    /// </summary>
    private static bool AssetsFileFound(Project project, string projectFile)
    {
        if (Path.GetDirectoryName(projectFile) is { } projectDir
            && File.Exists(Path.Combine(projectDir, "obj", AssetsFileName)))
            return true;

        foreach (var config in project.AnalyzerConfigDocuments)
        {
            if (config.FilePath is not { } path
                || !path.EndsWith(".GeneratedMSBuildEditorConfig.editorconfig", StringComparison.OrdinalIgnoreCase))
                continue;

            var dir = Path.GetDirectoryName(path);
            for (var level = 0; dir != null && level < 4; level++, dir = Path.GetDirectoryName(dir))
            {
                if (File.Exists(Path.Combine(dir, AssetsFileName))) return true;
                if (string.Equals(Path.GetFileName(dir), "obj", StringComparison.OrdinalIgnoreCase)) break;
            }
        }
        return false;
    }

    /// <summary>
    /// The compile errors of every loaded project, timed as a phase of its own.
    /// GetCompilationAsync succeeds on code that does not compile -- a compilation
    /// with errors is still a compilation -- so without this a broken project
    /// yields a quietly thinner graph. Asking costs a full bind of every method
    /// body, which is why it is its own phase in the timings: that cost is visible
    /// rather than folded into compile.
    /// </summary>
    private void RecordCompileErrors(CancellationToken ct)
    {
        Clock?.Begin(BuildPhases.CheckErrors);
        for (var i = 0; i < _loaded.Count; i++)
        {
            var loaded = _loaded[i];
            Clock?.Project(i + 1, _loaded.Count, loaded.Name);

            // A project that did not load compiles from no documents, so its
            // "errors" are only that emptiness (an exe with no Main) and would
            // bury the load failure that explains them.
            if (_failures.Any(f => f.Name == loaded.Name && f.Headers.Contains(DidNotLoad))) continue;

            var errors = loaded.Compilation.GetDiagnostics(ct)
                .Where(d => d.Severity == DiagnosticSeverity.Error)
                .Select(d => (Diagnostic: d, Span: d.Location.GetMappedLineSpan()))
                .OrderBy(e => e.Span.Path, StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => e.Span.StartLinePosition.Line)
                .ToList();
            if (errors.Count == 0) continue;

            var failure = FailureFor(loaded.Project, loaded.Name);
            failure.Headers.Add($"{errors.Count} compile error(s), the graph holds what Roslyn could bind of it");
            var projectDir = loaded.FilePath is { } file ? Path.GetDirectoryName(file) : null;
            foreach (var (diagnostic, span) in errors.Take(ErrorsListedPerProject))
                failure.Details.Add(DescribeError(diagnostic, span, projectDir));
        }
    }

    /// <summary>file:line: id: message, the path relative to the project when it lives inside it.</summary>
    private static string DescribeError(Diagnostic diagnostic, FileLinePositionSpan span, string? projectDir)
    {
        var message = diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture);
        if (!span.IsValid || string.IsNullOrEmpty(span.Path)) return $"{diagnostic.Id}: {message}";

        var path = projectDir != null ? Path.GetRelativePath(projectDir, span.Path) : span.Path;
        return $"{path}:{span.StartLinePosition.Line + 1}: {diagnostic.Id}: {message}";
    }

    /// <summary>Test projects skipped by the most recent load, empty unless exclusion was asked for.</summary>
    public IReadOnlyList<string> SkippedTestProjects => _skippedTestProjects;
    private readonly List<string> _skippedTestProjects = new();

    /// <summary>
    /// Reasons this build's SDK analyzers may not have run: our Roslyn trailing
    /// the SDK's, and any analyzer that actually failed to load. Empty is the
    /// normal case and means the generators ran. See AnalyzerHostCheck for why
    /// this cannot be left to surface on its own.
    /// </summary>
    public IReadOnlyList<string> AnalyzerHostWarnings => _analyzerHostWarnings;
    private readonly List<string> _analyzerHostWarnings = new();

    /// <summary>
    /// Arm both host checks for one project. Called before the compilation is
    /// requested, because requesting it is what loads the analyzers.
    /// </summary>
    private void WatchAnalyzerHost(Project project)
    {
        AnalyzerHostCheck.WatchLoadFailures(project, _analyzerHostWarnings);

        // Once per load, not once per project: the host and the SDK are the
        // same for every project in a solution, and repeating the line would
        // bury the per-project failures under it.
        if (_versionChecked) return;
        _versionChecked = true;

        var warning = AnalyzerHostCheck.VersionWarning(
            AnalyzerHostCheck.HostRoslynVersion(),
            AnalyzerHostCheck.SdkRoslynVersion(Registration.Value?.MSBuildPath));
        if (warning != null) _analyzerHostWarnings.Add(warning);
    }

    private bool _versionChecked;

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
    internal IEnumerable<SymbolInfo> ExtractSymbols()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();

        // Gathered once, ahead of the walk: a registration attribute sits on the
        // implementation, so the interface it names has nothing on its own
        // symbol for ClassifySymbol to read.
        var registeredServices = new HashSet<string>();
        foreach (var l in _loaded)
            registeredServices.UnionWith(SymbolClassifier.RegisteredServiceTypes(l.Compilation, inScope, Policy));

        var compiledItemsByLoaded = new Dictionary<LoadedProject, Dictionary<INamedTypeSymbol, string>>();

        foreach (var (loaded, tree) in _loaded.SelectMany(l => l.Compilation.SyntaxTrees.Select(t => (l, t))))
        {
            var model = loaded.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var info = SymbolClassifier.ClassifySymbol(
                    symbol, loaded.Name, loaded.Compilation, inScope, registeredServices, Policy);
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

            // A top-level program has no type declaration for the walk above to
            // find; its Program class and <Main>$ exist only as symbols, reached
            // through the entry point. See TopLevelProgram.
            //
            // Skipped when the type also has a real declaration: the ASP.NET SDK
            // emits a `public partial class Program` so WebApplicationFactory
            // <Program> can bind, and the walk above already yielded it. That
            // generated partial is also why this gap survived so long — every web
            // fixture in this repo gets a Program node by accident, and only a
            // plain console app shows the hole.
            if (TopLevelProgram.EntryPointUnit(root) is { } unit
                && TopLevelProgram.DeclaredMethod(unit, model) is { ContainingType: { } programType }
                && !programType.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is TypeDeclarationSyntax))
            {
                var info = SymbolClassifier.ClassifySymbol(
                    programType, loaded.Name, loaded.Compilation, inScope, registeredServices, Policy);
                if (info != null) yield return info;
            }
        }
    }

    /// <summary>
    /// Hand-written assembly and module attributes, per loaded project — the
    /// project layer's counterpart to a type's attribute list. See
    /// SymbolClassifier.ExtractAssemblyAttributes for why generated sites are
    /// excluded.
    /// </summary>
    internal IEnumerable<(string ProjectName, List<AttributeUsage> Attributes)> ExtractAssemblyAttributes()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = InScopeAssemblies();
        foreach (var loaded in _loaded)
            yield return (loaded.Name, SymbolClassifier.ExtractAssemblyAttributes(loaded.Compilation, inScope));
    }

    /// <summary>
    /// The call, construction and member-access sites of every loaded project,
    /// with the quoted names and callback targets, from one walk that asks each
    /// syntax tree for its semantic model once and hands that model to all five
    /// scans.
    /// </summary>
    /// <remarks>
    /// One model per tree is the point. A semantic model binds a method body the
    /// first time it is asked about it and keeps the result, but the cache lives
    /// in the model, not the compilation, and GetSemanticModel returns a new model
    /// every time. Three passes each calling it bound every body in the solution
    /// three times, and the second and third bindings were most of what the
    /// construction and member-access scans cost: on RazorGraphTool.slnx the
    /// construction scan fell from 1.4 s to 0.1 s, and member access from 1.9 s
    /// to 1.3 s, once they shared the call scan's model. The model goes when its
    /// tree is done, so no more than one tree's bindings are held at a time.
    ///
    /// The quoted-name and callback scans were two more passes with a model each,
    /// and their own phase; on the shared model they cost next to nothing, and
    /// that phase went from about 1.5 s to 0.01 s on RazorGraphTool.slnx and
    /// from about 1.1 s on eShopOnWeb. So they joined this walk, and their phase
    /// joined the calls phase, whose name says so -- a phase that took 0.01 s
    /// under a name promising the literals work would have reported that work as
    /// free and charged it to calls.
    ///
    /// Each list is in the order its own pass produced before the walks were
    /// fused -- project, then tree, then the scan's own order within the tree --
    /// so the emitters see the sequences they always did.
    /// </remarks>
    internal UsageSites ExtractUsageSites()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        return UsageSitesOf(_loaded.Select(l => l.Compilation), InScopeAssemblies());
    }

    /// <summary>
    /// The walk behind <see cref="ExtractUsageSites"/>, over any compilations.
    /// </summary>
    internal static UsageSites UsageSitesOf(IEnumerable<Compilation> compilations, IReadOnlySet<string> inScope)
    {
        var sites = new UsageSites([], [], [], [], []);

        foreach (var compilation in compilations)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);

                sites.Calls.AddRange(CallSitesIn(root, model, inScope));
                sites.Constructions.AddRange(ConstructionSitesIn(root, model, inScope));
                sites.MemberAccesses.AddRange(MemberAccessScanner.TreeMemberAccesses(root, model, inScope));
                sites.QuotedNames.AddRange(QuotedNameScanner.TreeQuotedNames(root, model));
                sites.CallbackTargets.AddRange(CallbackTargetsIn(root, model, inScope));
            }
        }

        return sites;
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
    ///
    /// Property and indexer accessor bodies are walked too, with the PROPERTY as
    /// the near end: accessors are not graph nodes. The edges exist in the graph
    /// for navigation; coverage and escape propagation do not step through a
    /// property node, so a method reached only through a getter is still
    /// uncovered.
    /// </summary>
    internal static IEnumerable<CallSiteInfo> CallSitesIn(SyntaxNode root, SemanticModel model, IReadOnlySet<string> inScope)
    {
        foreach (var site in TopLevelProgram.MethodScopes(root)
            .SelectMany(scope => CallSiteScanner.MethodCallSites(scope, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<BasePropertyDeclarationSyntax>()
            .SelectMany(property => CallSiteScanner.PropertyCallSites(property, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SelectMany(typeDecl => CallSiteScanner.InitializerCallSites(typeDecl, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SelectMany(typeDecl => CallSiteScanner.StaticInitializerCallSites(typeDecl, model, inScope)))
        {
            yield return site;
        }
    }

    /// <summary>
    /// Every place an in-solution type is built with a new-expression, from the
    /// member that builds it.
    /// </summary>
    /// <remarks>
    /// A separate scan from <see cref="CallSitesIn"/> rather than a second
    /// consumer of it, because the two keep different things: a call site keeps
    /// the CONSTRUCTOR and is dropped when that constructor has no node, which is
    /// exactly the case this edge exists for — a type whose constructor the
    /// compiler supplied had no incoming edge of any kind and read as dead.
    /// </remarks>
    internal static IEnumerable<ConstructionSiteInfo> ConstructionSitesIn(SyntaxNode root, SemanticModel model, IReadOnlySet<string> inScope)
    {
        foreach (var site in TopLevelProgram.MethodScopes(root)
            .SelectMany(scope => CallSiteScanner.ConstructionSites(scope, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<BasePropertyDeclarationSyntax>()
            .SelectMany(property => CallSiteScanner.PropertyConstructionSites(property, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SelectMany(typeDecl => CallSiteScanner.InitializerConstructionSites(typeDecl, model, inScope)))
        {
            yield return site;
        }

        foreach (var site in root.DescendantNodes()
            .OfType<TypeDeclarationSyntax>()
            .SelectMany(typeDecl => CallSiteScanner.StaticInitializerConstructionSites(typeDecl, model, inScope)))
        {
            yield return site;
        }
    }

    /// <summary>
    /// The views each controller action renders, the shape names display drivers
    /// and shape factories produce, and every controller action with the name
    /// routing knows it by, from one walk that asks each syntax tree for its
    /// semantic model once and hands that model to all three scans; see
    /// ViewCallScanner. View names are not resolved to files here -- that needs
    /// the Razor layer, which is built later.
    /// </summary>
    /// <remarks>
    /// One model per tree for the reason <see cref="ExtractUsageSites"/> gives:
    /// the three scans used to ask for a model each, so every declaration they
    /// looked at was bound three times. The stakes are smaller here than there.
    /// These scans bind a declaration's symbol and, in a controller, only its
    /// View and PartialView calls -- not whole bodies -- so the three passes cost
    /// 0.12-0.15 s on eShopOnWeb and 0.06 s on RazorGraphTool.slnx, and the one
    /// walk 0.06-0.10 s and 0.07-0.09 s.
    ///
    /// A walk of its own rather than three more lists on UsageSitesOf, because
    /// sharing the calls walk's model buys nothing: run there, on a model whose
    /// bodies were already bound, the three scans still cost 0.10 s on eShopOnWeb
    /// and 0.12 s on RazorGraphTool.slnx. Their only consumer is the binding pass
    /// in the cross-project links phase, after the Razor layer exists; riding the
    /// calls walk would charge view and action work to a phase named for calls and
    /// literals, and hold three lists across the phases in between for nothing.
    ///
    /// Each list is in the order its own pass produced before the walks were
    /// fused -- project, then tree, then declaration order within the tree -- so
    /// the binding pass sees the sequences it always did.
    /// </remarks>
    internal ViewBindingSites ExtractViewBindingSites()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        // Decided once per project, not once per shape: a test fixture naming a
        // shape is not a render path, and reporting it as one puts noise in the
        // only stream the shape scan exists to produce. OrchardCore's whole
        // solution surfaced exactly one such name (ContentZone, from
        // DisplayDriverTestHelper) among four.
        return ViewBindingSitesOf(_loaded.Select(l => (l.Compilation, IsTestProject(l.Project))));
    }

    /// <summary>
    /// The walk behind <see cref="ExtractViewBindingSites"/>, over any
    /// compilations, each with whether it is test code.
    /// </summary>
    internal static ViewBindingSites ViewBindingSitesOf(IEnumerable<(Compilation Compilation, bool InTestCode)> projects)
    {
        var sites = new ViewBindingSites([], [], []);

        foreach (var (compilation, inTestCode) in projects)
        {
            foreach (var tree in compilation.SyntaxTrees)
            {
                var root = tree.GetRoot();
                var model = compilation.GetSemanticModel(tree);

                sites.ViewCalls.AddRange(ViewCallsIn(root, model));
                sites.ShapeNames.AddRange(ShapeNamesIn(root, model, inTestCode));
                sites.ActionMethods.AddRange(ActionMethodsIn(root, model));
            }
        }

        return sites;
    }

    /// <summary>The views rendered by the methods in one tree; see ViewCallScanner.ViewCalls.</summary>
    internal static IEnumerable<ViewCall> ViewCallsIn(SyntaxNode root, SemanticModel model) =>
        root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .SelectMany(decl => ViewCallScanner.ViewCalls(decl, model));

    /// <summary>
    /// Shape names produced in one tree; see ViewCallScanner.ShapeNames. Empty on
    /// a solution with no display drivers or shape factories, which is every
    /// solution that is not OrchardCore-shaped.
    /// </summary>
    internal static IEnumerable<ShapeReference> ShapeNamesIn(SyntaxNode root, SemanticModel model, bool inTestCode) =>
        root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .SelectMany(decl => ViewCallScanner.ShapeNames(decl, model))
            .Select(shape => inTestCode ? shape with { InTestCode = true } : shape);

    /// <summary>
    /// The controller actions declared in one tree; see
    /// ViewCallScanner.ActionMethodOf. Needed to attribute a helper's render to
    /// the action that invoked it, which is the only thing that knows the view name.
    /// </summary>
    internal static IEnumerable<ActionMethod> ActionMethodsIn(SyntaxNode root, SemanticModel model) =>
        root.DescendantNodes()
            .OfType<BaseMethodDeclarationSyntax>()
            .Select(decl => ViewCallScanner.ActionMethodOf(decl, model))
            .OfType<ActionMethod>();

    /// <summary>
    /// Methods in one tree that out-of-solution code can call back into; see
    /// CallSiteScanner.CallbackTargets.
    /// </summary>
    internal static IEnumerable<string> CallbackTargetsIn(SyntaxNode root, SemanticModel model, IReadOnlySet<string> inScope)
    {
        // Top-level scopes included: a minimal API registers its whole
        // surface from Main — app.MapGet("/", Handler) hands a method group
        // to the framework, and that is a callback entry point exactly as a
        // registration inside a Startup class would be.
        foreach (var scope in TopLevelProgram.MethodScopes(root))
        {
            foreach (var targetId in CallSiteScanner.CallbackTargets(scope, model, inScope))
                yield return targetId;
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
