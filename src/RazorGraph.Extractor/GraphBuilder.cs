namespace RazorGraph.Extractor;

using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Client;
using RazorGraph.Extractor.Razor;
using RazorGraph.Extractor.Roslyn;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// Orchestrates Roslyn + Razor extraction into a unified CodeGraph.
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

    /// <summary>
    /// Full thrown-type records (with ancestor chains) per method id, kept for
    /// the escape sweep. The node property carries only the type names; the
    /// chains are build-time working data, not graph data.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<ThrownType>> _methodThrows = new(StringComparer.Ordinal);

    /// <summary>Type full name → its node id; see AddSymbolNode.</summary>
    private readonly Dictionary<string, string> _typeIdByFullName = new(StringComparer.Ordinal);

    /// <summary>Symbol ids whose member References edges are already emitted; see AddMemberTypeReferences.</summary>
    private readonly HashSet<string> _memberRefsEmitted = new(StringComparer.Ordinal);

    /// <summary>
    /// Graph vendor and minified client assets instead of dropping them. Vendor
    /// detection always runs; this switches the policy from drop to keep, for
    /// when the bug being hunted lives inside a shipped bundle. Included vendor
    /// nodes carry vendor=true and a vendorReason so queries can still tell the
    /// tiers apart.
    /// </summary>
    public bool IncludeVendorAssets { get; set; }

    /// <summary>One line per project whose client-asset scan dropped vendor files.</summary>
    public IReadOnlyList<string> AssetSkipSummaries => _assetSkipSummaries;
    private readonly List<string> _assetSkipSummaries = new();

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
        BuildRazorLayer(projectDir, idScope: null, _roslyn.Compilation);
        AddGeneratedClassLinks();
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

            BuildRazorLayer(projectDir, loaded.Name, loaded.Compilation);
        }

        AddGeneratedClassLinks();
        AddProjectNodes();
        AddCoverageEdges();
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
            AddSymbolNode(sym);
            AddMethodNodes(sym);
            AddMemberNodes(sym);
        }

        foreach (var sym in symbols)
        {
            AddInheritanceEdges(sym);
            AddMemberTypeReferences(sym);
        }

        AddExtensionEdges();
        AddMethodImplementsEdges();

        foreach (var sym in symbols)
        {
            AddInjectionEdges(sym);
        }

        AddCallEdges();
        AddMemberAccessEdges();
        AddCallbackEntryPoints();
        AddExceptionEscapeEdges();
    }

    /// <summary>
    /// One escaping-exception fact during the sweep: the type's ancestor
    /// chain, whether the only handling seen so far was a filtered catch, and
    /// the first hop toward the origin — BFS parent pointers, so one
    /// representative shortest path is reconstructible per (method, type).
    /// </summary>
    private readonly record struct EscapeFact(
        IReadOnlyList<string> AncestorChain, bool Conditional, string? FirstHop);

    /// <summary>
    /// Propagates locally-unhandled throws caller-ward over the Calls edges,
    /// stopping where an edge's guard set handles the type, and emits an
    /// Escapes edge for every (entry point, exception type) the worklist
    /// reaches — the precomputed answer to "what can crash this process".
    /// Monotone on a finite lattice (methods × thrown types, conditional
    /// upgradeable to firm once), so indirect recursion terminates without
    /// special handling. Runs in project and solution builds alike: escapes
    /// need no project boundary, unlike coverage.
    /// </summary>
    private void AddExceptionEscapeEdges()
    {
        var state = new Dictionary<string, Dictionary<string, EscapeFact>>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var (methodId, throws) in _methodThrows)
        {
            if (!_graph.HasNode(methodId)) continue;

            var facts = state[methodId] = new Dictionary<string, EscapeFact>(StringComparer.Ordinal);
            foreach (var thrown in throws)
                facts[thrown.Type] = new EscapeFact(thrown.AncestorChain, thrown.Conditional, FirstHop: null);
            queue.Enqueue(methodId);
        }

        while (queue.Count > 0)
        {
            var methodId = queue.Dequeue();
            var facts = state[methodId];

            foreach (var edge in CallerEdges(methodId))
            {
                var guardedBy = edge.GetProperty<List<string>>("guardedBy");
                var filteredBy = edge.GetProperty<List<string>>("filteredBy");
                var changed = false;

                foreach (var (type, fact) in facts)
                {
                    if (Handles(guardedBy, fact.AncestorChain)) continue;

                    var conditional = fact.Conditional || Handles(filteredBy, fact.AncestorChain);
                    if (!state.TryGetValue(edge.FromId, out var callerFacts))
                        callerFacts = state[edge.FromId] = new Dictionary<string, EscapeFact>(StringComparer.Ordinal);

                    if (callerFacts.TryGetValue(type, out var existing))
                    {
                        // Only a conditional→firm upgrade is new information.
                        if (!existing.Conditional || conditional) continue;
                    }

                    callerFacts[type] = new EscapeFact(fact.AncestorChain, conditional, methodId);
                    changed = true;
                }

                if (changed) queue.Enqueue(edge.FromId);
            }
        }

        // Boundary methods that deliberately absorb exceptions on the HTTP
        // pipeline. Escapes into HTTP-shaped entry points are matched against
        // them: intercepted means "shaped response, by design" — a
        // ValidationException becoming a 400 — while unmatched means a raw
        // 500. Statically the pipeline order and Map branches are unknowable,
        // so this is catch-set matching, and it never suppresses a report.
        var boundaries = _graph.NodesOfType(NodeType.Method)
            .Select(n => (n.Id,
                Firm: n.GetProperty<List<string>>("boundaryCatches"),
                Filtered: n.GetProperty<List<string>>("boundaryCatchesFiltered")))
            .Where(b => b.Firm != null || b.Filtered != null)
            .ToList();

        foreach (var entry in _graph.NodesOfType(NodeType.Method).ToList())
        {
            if (entry.GetProperty<string>("entryPointKind") is not { } kind) continue;
            if (!state.TryGetValue(entry.Id, out var facts)) continue;

            foreach (var (type, fact) in facts)
            {
                var path = ReconstructPath(entry.Id, type, fact, state);

                var edge = new GraphEdge
                {
                    FromId = path[0],
                    ToId = entry.Id,
                    Type = EdgeType.Escapes
                };
                edge.Properties["exceptionType"] = type;
                edge.Properties["depth"] = path.Count - 1;
                edge.Properties["path"] = path;
                if (fact.Conditional) edge.Properties["conditional"] = true;

                if (kind is "pageHandler" or "controllerAction" or "middleware")
                {
                    var firmHits = boundaries
                        .Where(b => b.Id != entry.Id && Handles(b.Firm, fact.AncestorChain))
                        .Select(b => b.Id)
                        .ToList();
                    var conditionalHits = boundaries
                        .Where(b => b.Id != entry.Id && !firmHits.Contains(b.Id)
                            && Handles(b.Filtered, fact.AncestorChain))
                        .Select(b => b.Id)
                        .ToList();
                    if (firmHits.Count > 0) edge.Properties["interceptedBy"] = firmHits;
                    if (conditionalHits.Count > 0) edge.Properties["interceptedConditionallyBy"] = conditionalHits;
                }

                _graph.AddEdge(edge);
            }
        }

        static bool Handles(List<string>? guards, IReadOnlyList<string> ancestorChain) =>
            guards != null
            && (guards.Contains("*") || guards.Intersect(ancestorChain, StringComparer.Ordinal).Any());
    }

    /// <summary>
    /// The edges that bring an escaping exception to more callers: direct
    /// incoming Calls, plus incoming Calls of every interface method this one
    /// implements — the caller bound to the interface, but this body is what
    /// runs, so its throws are what arrive. Conservative across multiple
    /// implementations: any implementation's throws reach the caller.
    /// </summary>
    private IEnumerable<GraphEdge> CallerEdges(string methodId)
    {
        foreach (var edge in _graph.Incoming(methodId))
            if (edge.Type == EdgeType.Calls) yield return edge;

        if (_graph.GetNode(methodId)?.GetProperty<List<string>>("implementsMethods") is not { } interfaces)
            yield break;

        foreach (var interfaceMethodId in interfaces)
            foreach (var edge in _graph.Incoming(interfaceMethodId))
                if (edge.Type == EdgeType.Calls) yield return edge;
    }

    /// <summary>
    /// Walks the first-hop pointers from an entry point back to the throw's
    /// origin and returns the path thrower-first. The seen-set is defensive:
    /// hop pointers are BFS parents and cannot cycle, but a truncated path
    /// beats an infinite loop if that invariant ever breaks.
    /// </summary>
    private static List<string> ReconstructPath(
        string entryId, string type, EscapeFact fact,
        Dictionary<string, Dictionary<string, EscapeFact>> state)
    {
        var path = new List<string> { entryId };
        var seen = new HashSet<string>(StringComparer.Ordinal) { entryId };

        var cursor = fact;
        while (cursor.FirstHop is { } next && seen.Add(next))
        {
            path.Add(next);
            cursor = state[next][type];
        }

        path.Reverse();
        return path;
    }

    /// <summary>
    /// Phases 5-8 for one project: Razor files, their correlation to the symbols
    /// already in the graph, partial cross-references, and client assets.
    /// </summary>
    private void BuildRazorLayer(string projectDir, string? idScope, Compilation? compilation)
    {
        var razorFiles = Directory.EnumerateFiles(projectDir, "*.cshtml", SearchOption.AllDirectories)
            .Concat(Directory.EnumerateFiles(projectDir, "*.razor", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                     && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var razorExtractor = new RazorExtractor(projectDir, idScope);
        TryProvideTagHelpers(razorExtractor, compilation);
        var razorInfos = new List<RazorPageInfo>();

        foreach (var file in razorFiles)
        {
            try
            {
                var info = razorExtractor.ExtractPage(file);
                razorInfos.Add(info);
                AddRazorNode(info, idScope);
            }
            catch (Exception ex)
            {
                // Log and continue — malformed Razor shouldn't kill the build
                Console.Error.WriteLine($"Warning: Failed to parse {file}: {ex.Message}");
            }
        }

        foreach (var info in razorInfos)
        {
            CorrelateRazorToRoslyn(info, _symbols, idScope);
        }

        foreach (var info in razorInfos)
        {
            AddPartialEdges(info, razorInfos);
        }

        AddClientAssets(projectDir, razorInfos, idScope);
    }

    /// <summary>
    /// Best-effort: discover tag helper descriptors from the loaded compilation so the
    /// Razor parser can bind tag helper elements. Failure is non-fatal — the extractor's
    /// text scan still captures asp-* attributes.
    /// </summary>
    private void TryProvideTagHelpers(RazorExtractor razorExtractor, Compilation? compilation)
    {
        if (compilation == null) return;

        try
        {
            var references = compilation.References
                .Concat(new MetadataReference[] { compilation.ToMetadataReference() })
                .ToList();

            var discoveryEngine = RazorProjectEngine.Create(
                RazorConfiguration.Default,
                RazorProjectFileSystem.Create(Directory.GetCurrentDirectory()),
                builder =>
                {
                    builder.Features.Add(new CompilationTagHelperFeature());
                    builder.Features.Add(new DefaultMetadataReferenceFeature { References = references });
                    builder.Features.Add(new DefaultTagHelperDescriptorProvider());
                });

            var descriptors = discoveryEngine.Engine.Features
                .OfType<CompilationTagHelperFeature>()
                .First()
                .GetDescriptors();

            if (descriptors.Count > 0) razorExtractor.SetTagHelpers(descriptors);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Warning: tag helper discovery failed ({ex.GetType().Name}); asp-* extraction continues via text scan.");
        }
    }

    private void AddSymbolNode(SymbolInfo sym)
    {
        var node = new GraphNode
        {
            Id = sym.Id,
            Type = sym.Type,
            Name = sym.Name,
            FilePath = sym.FilePath,
            LineStart = sym.LineStart,
            LineEnd = sym.LineEnd
        };

        node.SetProperty("fullName", sym.FullName);
        if (sym.Project != null) node.SetProperty("project", sym.Project);
        if (sym.BaseType != null) node.SetProperty("baseType", sym.BaseType);
        if (sym.GeneratedFrom != null) node.SetProperty("generatedFrom", sym.GeneratedFrom);
        StampGenerated(node);
        if (sym.Properties.Count > 0) node.SetProperty("properties", sym.Properties.Select(p => p.Name).ToList());
        if (sym.Methods.Count > 0) node.SetProperty("methods", sym.Methods.Select(m => m.Name).ToList());
        if (sym.InjectedServices.Count > 0) node.SetProperty("injectedServices", sym.InjectedServices);

        _graph.AddNode(node);

        // Full name → node id, for resolving member declared types to nodes
        // later: the id prefix depends on how the type classified (type:, vm:,
        // pm:, ...), so a name alone cannot be turned into an id. First
        // declaration wins, matching the partial-class rule for methods.
        _typeIdByFullName.TryAdd(sym.FullName, sym.Id);
    }

    private void AddMemberNodes(SymbolInfo sym)
    {
        foreach (var member in sym.MemberNodes)
        {
            // Same partial-class rule as methods: first declaration wins.
            if (_graph.HasNode(member.Id)) continue;

            var node = new GraphNode
            {
                Id = member.Id,
                Type = member.Kind,
                Name = member.Name,
                FilePath = member.FilePath ?? sym.FilePath,
                LineStart = member.LineStart
            };

            node.SetProperty("memberType", member.MemberType);
            node.SetProperty("declaringType", sym.FullName);
            if (sym.Project != null) node.SetProperty("project", sym.Project);
            if (member.IsStatic) node.SetProperty("isStatic", true);
            if (member.IsConst) node.SetProperty("isConst", true);
            if (member.IsReadOnly) node.SetProperty("isReadOnly", true);
            if (member.HasBindProperty) node.SetProperty("bindProperty", true);
            node.SetProperty("isPublic", member.IsPublic);
            StampGenerated(node);

            _graph.AddNode(node);

            _graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = member.Id,
                Type = EdgeType.Contains
            });
        }
    }

    /// <summary>
    /// References edges from each member to the in-solution types its declared
    /// type mentions. Runs in the second pass because the name→id map is only
    /// complete once every symbol node exists. This edge is what lets a DTO
    /// that appears only in signatures answer "who uses this type": incoming
    /// References from the properties and fields typed by it.
    /// </summary>
    private void AddMemberTypeReferences(SymbolInfo sym)
    {
        // A partial class yields one SymbolInfo per declaration, each carrying
        // the FULL member list (symbol members, not declaration members) — the
        // same first-declaration-wins rule the node adders apply, or every
        // member References edge doubles. Found on RetirementCore, where the
        // MVVM toolkit's generator makes half the view models partial.
        if (!_memberRefsEmitted.Add(sym.Id)) return;

        foreach (var member in sym.MemberNodes)
        {
            foreach (var typeName in member.ReferencedTypeFullNames)
            {
                if (!_typeIdByFullName.TryGetValue(typeName, out var typeId)) continue;

                _graph.AddEdge(new GraphEdge
                {
                    FromId = member.Id,
                    ToId = typeId,
                    Type = EdgeType.References
                });
            }
        }
    }

    /// <summary>
    /// A node whose final, #line-mapped position still lands in a .g.cs is
    /// generated scaffolding: nothing a reader can navigate to as authored
    /// code. Marked, not dropped — its semantics (calls, throws) are real.
    /// The rule is position-based on purpose: a @functions method authored in
    /// a .cshtml maps back to the .cshtml and is NOT marked, even though the
    /// compiler met it inside a generated file.
    /// </summary>
    private static void StampGenerated(GraphNode node)
    {
        if (node.FilePath?.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) == true)
            node.SetProperty("generated", true);
    }

    /// <summary>
    /// Wire each Razor node to the class its file was compiled into, joining
    /// the Razor layer and the Roslyn layer views of the same artifact. The
    /// edge reuses References with compiledInto=true rather than a new
    /// EdgeType: the serializer writes enum names as strings, so a new member
    /// would make every new graph unreadable by older deserializers.
    /// </summary>
    private void AddGeneratedClassLinks()
    {
        var razorByPath = _graph.Nodes
            .Where(n => n.Type is NodeType.RazorPage or NodeType.PartialView or NodeType.Layout)
            .Where(n => n.FilePath != null)
            .GroupBy(n => NormalizeFullPath(n.FilePath!))
            .ToDictionary(g => g.Key, g => g.ToList());
        if (razorByPath.Count == 0) return;

        foreach (var sym in _symbols)
        {
            if (sym.GeneratedFrom == null) continue;
            if (!razorByPath.TryGetValue(NormalizeFullPath(sym.GeneratedFrom), out var razorNodes)) continue;

            foreach (var razorNode in razorNodes)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = razorNode.Id,
                    ToId = sym.Id,
                    Type = EdgeType.References,
                    Properties = { ["compiledInto"] = true }
                });
            }
        }
    }

    private static string NormalizeFullPath(string path) =>
        Path.GetFullPath(path).Replace('\\', '/').ToLowerInvariant();

    private void AddMemberAccessEdges()
    {
        // One Reads and/or one Writes edge per accessor→member pair, matching
        // the one-edge-per-pair rule for calls: the graph is navigation, not
        // a profile.
        foreach (var group in _roslyn.ExtractMemberAccesses().GroupBy(a => (a.FromId, a.ToId)))
        {
            var (fromId, toId) = group.Key;

            // Accesses from or to nodes the classifier skipped have nothing to anchor to.
            if (!_graph.HasNode(fromId) || !_graph.HasNode(toId)) continue;

            if (group.Any(a => a.IsRead))
                _graph.AddEdge(new GraphEdge { FromId = fromId, ToId = toId, Type = EdgeType.Reads });
            if (group.Any(a => a.IsWrite))
                _graph.AddEdge(new GraphEdge { FromId = fromId, ToId = toId, Type = EdgeType.Writes });
        }
    }

    private void AddMethodNodes(SymbolInfo sym)
    {
        foreach (var method in sym.MethodNodes)
        {
            // A partial class declared across two files yields the same method id
            // twice; the first declaration wins rather than duplicating the node.
            if (_graph.HasNode(method.Id)) continue;

            var node = new GraphNode
            {
                Id = method.Id,
                Type = NodeType.Method,
                Name = method.Name,
                FilePath = method.FilePath ?? sym.FilePath,
                LineStart = method.LineStart
            };

            node.SetProperty("signature", method.Signature);
            node.SetProperty("returnType", method.ReturnType);
            node.SetProperty("declaringType", sym.FullName);
            if (sym.Project != null) node.SetProperty("project", sym.Project);
            if (method.IsAsync) node.SetProperty("isAsync", true);
            if (method.IsStatic) node.SetProperty("isStatic", true);
            if (method.IsTest) node.SetProperty("isTest", true);
            if (method.IsTestLifecycle) node.SetProperty("isTestLifecycle", true);
            if (method.IsAbstract) node.SetProperty("isAbstract", true);
            if (method.NestingDepth > 0) node.SetProperty("bodyDepth", method.NestingDepth);
            if (method.Throws.Count > 0)
            {
                // Names on the node for display; the full ancestor chains stay
                // in memory for the escape sweep — persisting them would bloat
                // every Method node for data only the build consumes.
                node.SetProperty("throws", method.Throws.Select(t => t.Type).ToList());
                _methodThrows[method.Id] = method.Throws;
            }
            if (method.EntryPointKind is { } entryPointKind)
                node.SetProperty("entryPointKind", entryPointKind);
            if (method.ImplementsIds.Count > 0)
                node.SetProperty("implementsMethods", method.ImplementsIds);
            if (method.BoundaryCatches.Count > 0)
                node.SetProperty("boundaryCatches", method.BoundaryCatches);
            if (method.BoundaryCatchesFiltered.Count > 0)
                node.SetProperty("boundaryCatchesFiltered", method.BoundaryCatchesFiltered);
            node.SetProperty("isPublic", method.IsPublic);
            StampGenerated(node);

            _graph.AddNode(node);

            _graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = method.Id,
                Type = EdgeType.Contains
            });
        }
    }

    private void AddCallEdges()
    {
        // One edge per caller→callee pair because a caller invoking the same
        // method three times is one dependency, and the graph is read as
        // navigation rather than as a profile. The guard context of every site
        // rides along as the intersection across sites: an exception passes
        // this edge unless every site guards it, so a type missing from the
        // intersection is a possible escape — the conservative direction.
        foreach (var group in _roslyn.ExtractCallSites().GroupBy(s => (s.FromId, s.ToId)))
        {
            var (fromId, toId) = group.Key;

            // Calls into types the classifier skipped have no node to point at.
            if (!_graph.HasNode(fromId) || !_graph.HasNode(toId)) continue;

            var edge = new GraphEdge
            {
                FromId = fromId,
                ToId = toId,
                Type = EdgeType.Calls
            };

            var guardedBy = IntersectGuards(group.Select(s => s.GuardedBy));
            var filteredBy = IntersectGuards(group.Select(s => s.FilteredBy));
            if (guardedBy.Count > 0) edge.Properties["guardedBy"] = guardedBy;
            if (filteredBy.Count > 0) edge.Properties["filteredBy"] = filteredBy;

            // Marked only when the dependency exists purely through delegate
            // references, so consumers reading the graph as navigation can
            // tell "calls" from "may cause to run".
            if (group.All(s => s.IsDelegate)) edge.Properties["viaDelegate"] = true;

            _graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// Method-level Implements edges, implementation → interface method. The
    /// type-level edge says the class implements the interface; this one says
    /// which body actually runs when a caller binds to the interface member —
    /// the join escape propagation crosses DI on.
    /// </summary>
    private void AddMethodImplementsEdges()
    {
        foreach (var sym in _symbols)
            foreach (var method in sym.MethodNodes)
                foreach (var interfaceMethodId in method.ImplementsIds)
                {
                    if (!_graph.HasNode(method.Id) || !_graph.HasNode(interfaceMethodId)) continue;

                    _graph.AddEdge(new GraphEdge
                    {
                        FromId = method.Id,
                        ToId = interfaceMethodId,
                        Type = EdgeType.Implements
                    });
                }
    }

    /// <summary>
    /// Emits Extends edges from extension methods to the in-solution types
    /// they extend. Runs after every symbol is a node — extensions routinely
    /// live in a different file (and project) from the type they serve. An
    /// extension on an out-of-solution type (string, IEnumerable) has no node
    /// to point at and simply carries its extendsType property.
    /// </summary>
    private void AddExtensionEdges()
    {
        var idByFullName = _symbols
            .GroupBy(s => s.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        foreach (var sym in _symbols)
            foreach (var method in sym.MethodNodes)
            {
                if (method.ExtendsTypeFullName is not { } extended) continue;
                if (_graph.GetNode(method.Id) is { } node)
                    node.SetProperty("extendsType", extended);

                if (!idByFullName.TryGetValue(extended, out var typeId)) continue;
                if (!_graph.HasNode(method.Id) || !_graph.HasNode(typeId)) continue;

                _graph.AddEdge(new GraphEdge
                {
                    FromId = method.Id,
                    ToId = typeId,
                    Type = EdgeType.Extends
                });
            }
    }

    /// <summary>
    /// Stamps the callback entry points: methods out-of-solution code holds a
    /// delegate to. Runs after AddMethodNodes so an existing classification
    /// (an event-handler-shaped method registered to a framework event is
    /// both) keeps the more specific kind. Test methods are excluded for the
    /// same reason ClassifyEntryPoint excludes them — every Assert.Throws
    /// lambda hands out-of-solution, and the test host catches what tests
    /// throw.
    /// </summary>
    private void AddCallbackEntryPoints()
    {
        foreach (var targetId in _roslyn.ExtractCallbackTargets())
        {
            if (_graph.GetNode(targetId) is not { } node) continue;
            if (node.GetProperty<bool>("isTest") || node.GetProperty<bool>("isTestLifecycle")) continue;
            if (node.GetProperty<string>("entryPointKind") == null)
                node.SetProperty("entryPointKind", "callback");
        }
    }

    /// <summary>
    /// Intersection of per-site guard sets, where "*" (untyped catch-all) is
    /// the universe: it constrains nothing, and only survives when every site
    /// has it. The intersection is by exact type name — two sites catching
    /// different bases both able to stop the same exception intersect to
    /// empty, which over-reports escapes rather than hiding one.
    /// </summary>
    private static List<string> IntersectGuards(IEnumerable<IReadOnlyList<string>> sites)
    {
        List<string>? intersection = null;
        var allCatchAll = true;

        foreach (var site in sites)
        {
            if (site.Contains("*")) continue;

            allCatchAll = false;
            intersection = intersection == null
                ? site.Distinct().ToList()
                : intersection.Intersect(site, StringComparer.Ordinal).ToList();
        }

        return allCatchAll ? new List<string> { "*" } : intersection ?? new List<string>();
    }

    /// <summary>
    /// Adds JavaScript/CSS nodes and the three edges that cross the server/client
    /// boundary: which page loads an asset, which server-rendered data-* keys a
    /// script reads back, and which API routes a script calls.
    /// </summary>
    private void AddClientAssets(string projectDir, List<RazorPageInfo> razorInfos, string? idScope)
    {
        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(projectDir, idScope, IncludeVendorAssets);
        ReportVendorSkips(extractor.LastSkipped, projectDir);

        // Inline blocks are assets that happen to live in a .cshtml. Folding them
        // into the same list means every downstream step -- coupling edges,
        // unbound-key detection, JS-to-API binding -- treats them identically,
        // rather than each one having to remember the inline case exists.
        var inlineByPage = new Dictionary<string, List<ClientAssetInfo>>(StringComparer.Ordinal);
        foreach (var info in razorInfos)
        {
            foreach (var script in info.InlineScripts)
            {
                var inline = ClientAssetExtractor.BuildInlineScript(
                    idScope, info.RelativePath, info.FilePath, script.Body, script.Line, script.LineCount);

                assets.Add(inline);
                if (!inlineByPage.TryGetValue(info.Id, out var list))
                {
                    list = new List<ClientAssetInfo>();
                    inlineByPage[info.Id] = list;
                }
                list.Add(inline);
            }
        }

        if (assets.Count == 0) return;

        var byRelativePath = new Dictionary<string, ClientAssetInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var asset in assets)
        {
            var node = new GraphNode
            {
                Id = asset.Id,
                Type = asset.IsScript ? NodeType.JavaScriptFile : NodeType.CssFile,
                Name = asset.Name,
                FilePath = asset.FilePath,
                LineStart = asset.LineStart
            };

            node.SetProperty("relativePath", asset.RelativePath);
            node.SetProperty("lineCount", asset.LineCount);
            if (idScope != null) node.SetProperty("project", idScope);
            if (asset.IsInline) node.SetProperty("inline", true);
            if (asset.IsVendor)
            {
                node.SetProperty("vendor", true);
                if (asset.VendorReason != null) node.SetProperty("vendorReason", asset.VendorReason);
            }
            if (asset.DataKeys.Count > 0) node.SetProperty("dataKeys", asset.DataKeys.OrderBy(k => k).ToList());
            if (asset.DataKeysWritten.Count > 0)
                node.SetProperty("dataKeysWritten", asset.DataKeysWritten.OrderBy(k => k).ToList());
            if (asset.ApiCalls.Count > 0) node.SetProperty("apiCalls", asset.ApiCalls.OrderBy(u => u).ToList());
            if (asset.SelectorIds.Count > 0)
                node.SetProperty("selectorIds", asset.SelectorIds.OrderBy(i => i, StringComparer.Ordinal).ToList());
            if (asset.DynamicSelectorCount > 0)
                node.SetProperty("dynamicSelectorCount", asset.DynamicSelectorCount);

            _graph.AddNode(node);
            byRelativePath[asset.RelativePath] = asset;
        }

        var byId = razorInfos.ToDictionary(r => r.Id, r => r);
        // Which pages reference each script, so an unread data key can be
        // distinguished from one no page ever renders. Same shape for ids,
        // except ids are case-sensitive in the DOM.
        var renderedKeysByAsset = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var renderedIdsByAsset = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var assetsWithDynamicIdScope = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var info in razorInfos)
        {
            var scope = DataKeysInScope(info, byId, razorInfos, idScope);
            var referenced = ReferencedAssets(info, byId, byRelativePath);
            if (inlineByPage.TryGetValue(info.Id, out var inlineAssets))
                referenced = referenced.Concat(inlineAssets);

            foreach (var asset in referenced)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = asset.Id,
                    Type = EdgeType.References
                });

                if (!asset.IsScript) continue;

                if (!renderedKeysByAsset.TryGetValue(asset.Id, out var seen))
                {
                    seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    renderedKeysByAsset[asset.Id] = seen;
                }
                seen.UnionWith(scope.Rendered);

                if (!renderedIdsByAsset.TryGetValue(asset.Id, out var seenIds))
                {
                    seenIds = new HashSet<string>(StringComparer.Ordinal);
                    renderedIdsByAsset[asset.Id] = seenIds;
                }
                seenIds.UnionWith(scope.Ids);
                if (scope.DynamicIds > 0) assetsWithDynamicIdScope.Add(asset.Id);

                var shared = asset.DataKeys.Intersect(scope.ServerBound, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k).ToList();
                if (shared.Count > 0)
                {
                    // Direction matches FindServerToJsMismatches, which reads the
                    // server node off the incoming edge of the JS node.
                    _graph.AddEdge(new GraphEdge
                    {
                        FromId = info.Id,
                        ToId = asset.Id,
                        Type = EdgeType.ViewDataReadBy,
                        Properties = { ["dataKeys"] = shared }
                    });
                }

                // Self-created ids carry no server contract, so only foreign
                // selections can bind to what this page composition renders.
                var sharedIds = asset.SelectorIdsForeign.Intersect(scope.Ids, StringComparer.Ordinal)
                    .OrderBy(i => i, StringComparer.Ordinal).ToList();
                if (sharedIds.Count > 0)
                {
                    _graph.AddEdge(new GraphEdge
                    {
                        FromId = info.Id,
                        ToId = asset.Id,
                        Type = EdgeType.DomSelectedBy,
                        Properties = { ["ids"] = sharedIds }
                    });
                }
            }
        }

        AnnotateUnboundKeys(assets, renderedKeysByAsset);
        AnnotateUnboundSelectorIds(assets, renderedIdsByAsset, assetsWithDynamicIdScope);
        AddJsToApiEdges(assets);
    }

    /// <summary>
    /// A dropped vendor asset must leave a trace: a silent skip reads as
    /// "covered everything" when it did not. One summary line per project, on
    /// stderr for CLI runs and in <see cref="AssetSkipSummaries"/> for callers
    /// that return structured results.
    /// </summary>
    private void ReportVendorSkips(IReadOnlyList<ClientAssetExtractor.SkippedAsset> skipped, string projectDir)
    {
        if (skipped.Count == 0) return;

        var byReason = skipped
            .GroupBy(s => s.Reason)
            .OrderByDescending(g => g.Count())
            .Select(g => $"{g.Count()} x {g.Key}");
        var summary =
            $"Skipped {skipped.Count} vendor asset(s) under {Path.GetFileName(projectDir)}: " +
            $"{string.Join(", ", byReason)}. Enable include-vendor to graph them.";

        _assetSkipSummaries.Add(summary);
        Console.Error.WriteLine($"Info: {summary}");
    }

    /// <summary>
    /// data-* keys available to scripts on this page. A script sees one composed
    /// DOM, so the scope is the page plus its layout plus every partial either of
    /// them renders, transitively -- markup emitted by a partial is just as
    /// present as markup in the page itself.
    /// </summary>
    private static DataKeyScope DataKeysInScope(
        RazorPageInfo info,
        Dictionary<string, RazorPageInfo> byId,
        List<RazorPageInfo> allPages,
        string? idScope)
    {
        var serverBound = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rendered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<RazorPageInfo>();

        pending.Enqueue(info);
        if (info.Layout != null && byId.TryGetValue(RazorExtractor.PageId(idScope, info.Layout), out var layout))
            pending.Enqueue(layout);

        var renderedIds = new HashSet<string>(StringComparer.Ordinal);
        var dynamicIds = 0;

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            // Partials can render each other; visited keeps a cycle from hanging the build.
            if (!visited.Add(current.Id)) continue;

            serverBound.UnionWith(current.ServerDataKeys);
            rendered.UnionWith(current.RenderedDataKeys);
            renderedIds.UnionWith(current.RenderedIds);
            dynamicIds += current.DynamicIdCount;

            foreach (var partial in current.Partials)
            {
                var resolved = ResolvePartial(partial, allPages);
                if (resolved != null) pending.Enqueue(resolved);
            }
        }

        return new DataKeyScope(serverBound, rendered, renderedIds, dynamicIds);
    }

    /// <summary>
    /// What one composed page exposes to its scripts. ServerBound drives the
    /// data-key coupling edge; Rendered drives unbound-key detection; Ids
    /// (case-sensitive, unlike data-* keys) drive the selector contract; and
    /// DynamicIds counts id attributes rendered from Razor expressions — a
    /// scope containing any exposes ids under names no static scan can know,
    /// so it must stop accusing scripts of unbound selectors.
    /// </summary>
    private readonly record struct DataKeyScope(
        HashSet<string> ServerBound,
        HashSet<string> Rendered,
        HashSet<string> Ids,
        int DynamicIds);

    private static RazorPageInfo? ResolvePartial(PartialRenderInfo partial, List<RazorPageInfo> allPages) =>
        allPages.FirstOrDefault(p =>
            p.RelativePath.Contains(partial.Name, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ClientAssetInfo> ReferencedAssets(
        RazorPageInfo info,
        Dictionary<string, RazorPageInfo> byId,
        Dictionary<string, ClientAssetInfo> byRelativePath)
    {
        foreach (var href in info.AssetReferences)
        {
            var resolved = ClientAssetExtractor.ResolveAssetPath(href);
            if (resolved != null && byRelativePath.TryGetValue(resolved, out var asset))
                yield return asset;
        }
    }

    /// <summary>
    /// A data key a script only ever reads, that no page loading it renders, is
    /// the actionable half of the report -- a rename that broke one side only.
    /// Keys the script writes itself are excluded: that is client-owned state,
    /// not a broken contract with the server.
    /// </summary>
    private void AnnotateUnboundKeys(
        List<ClientAssetInfo> assets,
        Dictionary<string, HashSet<string>> renderedKeysByAsset)
    {
        foreach (var asset in assets)
        {
            if (!asset.IsScript || asset.DataKeys.Count == 0) continue;

            // No referencing page found means the script is loaded some way this
            // extractor cannot see; silence beats a false accusation.
            if (!renderedKeysByAsset.TryGetValue(asset.Id, out var rendered)) continue;

            var unbound = asset.DataKeysReadOnly.Except(rendered, StringComparer.OrdinalIgnoreCase)
                .OrderBy(k => k).ToList();
            if (unbound.Count == 0) continue;

            _graph.GetNode(asset.Id)?.SetProperty("unboundDataKeys", unbound);
        }
    }

    /// <summary>
    /// A selector id no referencing page renders is the id-contract defect —
    /// same rename-broke-one-side shape as an unbound data key. Reporting stays
    /// quiet unless the evidence is complete: a script with dynamic selector
    /// call sites cannot be fully seen, a page scope with dynamic ids exposes
    /// names no scan can know, and a script no page references may be loaded
    /// some way this extractor cannot see. Silence beats a false accusation.
    /// </summary>
    private void AnnotateUnboundSelectorIds(
        List<ClientAssetInfo> assets,
        Dictionary<string, HashSet<string>> renderedIdsByAsset,
        HashSet<string> assetsWithDynamicIdScope)
    {
        foreach (var asset in assets)
        {
            if (!asset.IsScript || !asset.SelectorIdsForeign.Any()) continue;
            if (asset.DynamicSelectorCount > 0) continue;
            if (assetsWithDynamicIdScope.Contains(asset.Id)) continue;
            if (!renderedIdsByAsset.TryGetValue(asset.Id, out var rendered)) continue;

            var unbound = asset.SelectorIdsForeign.Except(rendered, StringComparer.Ordinal)
                .OrderBy(i => i, StringComparer.Ordinal).ToList();
            if (unbound.Count == 0) continue;

            _graph.GetNode(asset.Id)?.SetProperty("unboundSelectorIds", unbound);
        }
    }

    /// <summary>
    /// Bind literal fetch URLs to the controller that serves them, matching
    /// /api/{controller}/... against the ApiController nodes already in the graph.
    /// </summary>
    private void AddJsToApiEdges(List<ClientAssetInfo> assets)
    {
        var controllers = _graph.NodesOfType(NodeType.ApiController).ToList();
        if (controllers.Count == 0) return;

        foreach (var asset in assets)
        {
            foreach (var url in asset.ApiCalls)
            {
                var segments = url.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length < 2 || !segments[0].Equals("api", StringComparison.OrdinalIgnoreCase))
                    continue;

                var name = segments[1];
                var controller = controllers.FirstOrDefault(c =>
                    c.Name.Equals($"{name}Controller", StringComparison.OrdinalIgnoreCase) ||
                    c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
                if (controller == null) continue;

                _graph.AddEdge(new GraphEdge
                {
                    FromId = asset.Id,
                    ToId = controller.Id,
                    Type = EdgeType.Calls,
                    Properties = { ["url"] = url }
                });
            }
        }
    }

    /// <summary>
    /// One node per project plus the reference edges between them. Cheap
    /// orientation for a solution graph: which assemblies exist and which way
    /// the dependencies point, without reading 900 nodes to infer it.
    /// </summary>
    private void AddProjectNodes()
    {
        var loaded = _roslyn.LoadedProjects;
        if (loaded.Count == 0) return;

        var nodeCounts = _graph.Nodes
            .Select(n => n.GetProperty<string>("project"))
            .Where(p => p != null)
            .GroupBy(p => p!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        foreach (var project in loaded)
        {
            var node = new GraphNode
            {
                Id = ProjectId(project.Name),
                Type = NodeType.Project,
                Name = project.Name,
                FilePath = project.FilePath
            };

            node.SetProperty("assemblyName", project.Compilation.AssemblyName ?? project.Name);
            node.SetProperty("nodeCount", nodeCounts.TryGetValue(project.Name, out var c) ? c : 0);
            _graph.AddNode(node);
        }

        var solution = _roslyn.Solution;
        if (solution == null) return;

        foreach (var project in loaded)
        {
            foreach (var reference in project.Project.ProjectReferences)
            {
                var target = solution.GetProject(reference.ProjectId);
                if (target == null || !_graph.HasNode(ProjectId(target.Name))) continue;

                _graph.AddEdge(new GraphEdge
                {
                    FromId = ProjectId(project.Name),
                    ToId = ProjectId(target.Name),
                    Type = EdgeType.DependsOn
                });
            }
        }
    }

    private static string ProjectId(string projectName) => $"proj:{projectName}";

    /// <summary>
    /// Link each test method to the production code it exercises.
    ///
    /// Reachability is followed through Calls to the full closure, not a fixed
    /// horizon: coverage is a reachability claim, and a truncated closure turns
    /// "no test reaches this" into a statement about the cutoff rather than the
    /// code. The depth each node was reached at rides on the edge, so a consumer
    /// who wants direct exercise alone filters on depth instead of the graph
    /// being pre-truncated for everyone. Edges are only emitted across a project
    /// boundary, since a test calling its own helpers is not coverage of
    /// anything. maxDepth remains as a guardrail for callers that want one.
    ///
    /// Traversal seeds from each test and from its class's lifecycle hooks
    /// (isTestLifecycle) alike: the framework runs InitializeAsync/[SetUp]
    /// around every test in the class, so work done there is exercised by each
    /// of them even though no test calls it. A node reachable from both seeds
    /// keeps the shallower depth.
    /// </summary>
    private void AddCoverageEdges(int maxDepth = int.MaxValue)
    {
        var methods = _graph.NodesOfType(NodeType.Method).ToList();

        var tests = methods.Where(n => n.GetProperty<bool>("isTest")).ToList();
        if (tests.Count == 0) return;

        var lifecycleByType = methods
            .Where(n => n.GetProperty<bool>("isTestLifecycle"))
            .GroupBy(n => n.GetProperty<string>("declaringType") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());

        var callsOnly = new HashSet<EdgeType> { EdgeType.Calls };

        foreach (var test in tests)
        {
            var testProject = test.GetProperty<string>("project");

            var seeds = new List<GraphNode> { test };
            var declaringType = test.GetProperty<string>("declaringType");
            if (declaringType != null && lifecycleByType.TryGetValue(declaringType, out var hooks))
                seeds.AddRange(hooks);

            // Materialised before edges are added: Traverse streams straight off
            // the adjacency lists, and adding a Covers edge below mutates one of
            // them mid-enumeration.
            var reachedAtDepth = new Dictionary<string, (GraphNode Node, int Depth)>();
            foreach (var seed in seeds)
            {
                foreach (var (node, _, depth) in _graph.Traverse(seed.Id, callsOnly, maxDepth).ToList())
                {
                    if (!reachedAtDepth.TryGetValue(node.Id, out var existing) || depth < existing.Depth)
                        reachedAtDepth[node.Id] = (node, depth);
                }
            }

            foreach (var (node, depth) in reachedAtDepth.Values)
            {
                if (node.Type != NodeType.Method) continue;
                if (node.GetProperty<bool>("isTest")) continue;
                if (node.GetProperty<bool>("isTestLifecycle")) continue;

                var project = node.GetProperty<string>("project");
                if (project == null || string.Equals(project, testProject, StringComparison.OrdinalIgnoreCase))
                    continue;

                _graph.AddEdge(new GraphEdge
                {
                    FromId = test.Id,
                    ToId = node.Id,
                    Type = EdgeType.Covers,
                    Properties = { ["depth"] = depth }
                });
            }
        }
    }

    private void AddInheritanceEdges(SymbolInfo sym)
    {
        if (string.IsNullOrWhiteSpace(sym.BaseType)) return;

        // Find base type node
        var baseNode = _graph.Nodes.FirstOrDefault(n =>
            (n.Type == NodeType.PageModel || n.Type == NodeType.Class) &&
            n.GetProperty<string>("fullName") == sym.BaseType);

        if (baseNode != null)
        {
            _graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = baseNode.Id,
                Type = EdgeType.Inherits
            });
        }
    }

    private void AddInjectionEdges(SymbolInfo sym)
    {
        foreach (var serviceType in sym.InjectedServices)
        {
            var serviceNode = FindServiceNode(serviceType);
            if (serviceNode != null)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = serviceNode.Id,
                    ToId = sym.Id,
                    Type = EdgeType.InjectedInto
                });
            }
        }
    }

    private void AddRazorNode(RazorPageInfo info, string? idScope)
    {
        var node = new GraphNode
        {
            Id = info.Id,
            Type = info.IsPage ? NodeType.RazorPage : NodeType.PartialView,
            Name = Path.GetFileNameWithoutExtension(info.RelativePath),
            FilePath = info.FilePath
        };

        if (idScope != null) node.SetProperty("project", idScope);
        if (info.InlineScripts.Count > 0) node.SetProperty("inlineScriptCount", info.InlineScripts.Count);
        if (info.RouteTemplate != null) node.SetProperty("routeTemplate", info.RouteTemplate);
        if (info.ModelType != null) node.SetProperty("modelType", info.ModelType);
        if (info.Layout != null) node.SetProperty("layout", info.Layout);
        if (info.ViewDataKeys.Count > 0) node.SetProperty("viewDataKeys", info.ViewDataKeys);
        if (info.Sections.Count > 0) node.SetProperty("sections", info.Sections);

        _graph.AddNode(node);
    }

    private void CorrelateRazorToRoslyn(RazorPageInfo info, List<SymbolInfo> symbols, string? idScope)
    {
        // Link page → PageModel
        if (info.ModelType != null)
        {
            var modelNode = symbols.FirstOrDefault(s =>
                s.Type == NodeType.PageModel &&
                (s.FullName == info.ModelType || s.Name == info.ModelType));

            if (modelNode != null)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = modelNode.Id,
                    Type = EdgeType.PageServedBy
                });

                // Link PageModel → page (bidirectional for queries)
                _graph.AddEdge(new GraphEdge
                {
                    FromId = modelNode.Id,
                    ToId = info.Id,
                    Type = EdgeType.ReturnsView
                });
            }
        }

        // Link page → ViewModel class
        if (info.ModelType != null)
        {
            var vmNode = _graph.Nodes.FirstOrDefault(n =>
                (n.Type == NodeType.ViewModel || n.Type == NodeType.Class) &&
                (n.GetProperty<string>("fullName") == info.ModelType || n.Name == info.ModelType));

            if (vmNode != null)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = vmNode.Id,
                    Type = EdgeType.HasModel
                });
            }
        }

        // Link page → layout
        if (info.Layout != null)
        {
            var layoutId = RazorExtractor.PageId(idScope, info.Layout);
            if (_graph.HasNode(layoutId))
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = layoutId,
                    Type = EdgeType.UsesLayout
                });
            }
        }

        // Link tag helpers to model properties
        foreach (var th in info.TagHelpers)
        {
            var aspFor = th.Attributes.FirstOrDefault(a => a.Name == "asp-for");
            if (aspFor != null)
            {
                var propName = aspFor?.Value?.Trim('"', '\'') ?? "";
                var thNodeId = $"th:{info.Id}:{th.Line}";
                var thNode = new GraphNode
                {
                    Id = thNodeId,
                    Type = NodeType.TagHelperInvocation,
                    Name = $"{th.TagName} asp-for=\"{propName}\"",
                    FilePath = info.FilePath,
                    LineStart = th.Line
                };
                thNode.SetProperty("property", propName);
                thNode.SetProperty("tagName", th.TagName);
                _graph.AddNode(thNode);

                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = thNodeId,
                    Type = EdgeType.RendersComponent
                });

                // Try to bind to ViewModel property
                if (info.ModelType != null)
                {
                    var vmNode = _graph.Nodes.FirstOrDefault(n =>
                        n.Name == info.ModelType || n.GetProperty<string>("fullName") == info.ModelType);

                    if (vmNode != null)
                    {
                        _graph.AddEdge(new GraphEdge
                        {
                            FromId = thNodeId,
                            ToId = vmNode.Id,
                            Type = EdgeType.BindsTo
                        });
                    }
                }
            }
        }
    }

    private void AddPartialEdges(RazorPageInfo info, List<RazorPageInfo> allPages)
    {
        foreach (var partial in info.Partials)
        {
            // Try to find the partial file
            var partialFile = allPages.FirstOrDefault(p =>
                p.RelativePath.Contains(partial.Name, StringComparison.OrdinalIgnoreCase));

            if (partialFile != null)
            {
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = partialFile.Id,
                    Type = EdgeType.RendersPartial,
                    Properties = { ["line"] = partial.Line, ["isTagHelper"] = partial.IsTagHelper }
                });
            }
            else
            {
                // Partial not found in parsed set — create a stub node
                var stubId = $"partial:{partial.Name}";
                if (!_graph.HasNode(stubId))
                {
                    _graph.AddNode(new GraphNode
                    {
                        Id = stubId,
                        Type = NodeType.PartialView,
                        Name = partial.Name
                    });
                }
                _graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = stubId,
                    Type = EdgeType.RendersPartial
                });
            }
        }
    }

    private GraphNode? FindServiceNode(string typeName)
    {
        // Try exact match
        var exact = _graph.Nodes.FirstOrDefault(n =>
            n.GetProperty<string>("fullName") == typeName);
        if (exact != null) return exact;

        // Try interface name (IImageService → ImageService)
        if (typeName.StartsWith("I") && typeName.Length > 1)
        {
            var implName = typeName[1..];
            return _graph.Nodes.FirstOrDefault(n =>
                n.Type == NodeType.ServiceImplementation && n.Name == implName);
        }

        return null;
    }

    public ValueTask DisposeAsync() => _roslyn.DisposeAsync();
}
