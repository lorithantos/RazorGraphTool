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
        await _roslyn.LoadAllProjectsAsync(solutionPath, ct);
        return BuildSolutionGraph();
    }

    private CodeGraph BuildGraph(string projectDir)
    {
        BuildRoslynLayer();
        BuildRazorLayer(projectDir, idScope: null, _roslyn.Compilation);
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
        }

        foreach (var sym in symbols)
        {
            AddInheritanceEdges(sym);
        }

        foreach (var sym in symbols)
        {
            AddInjectionEdges(sym);
        }

        AddCallEdges();
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
        if (sym.Properties.Count > 0) node.SetProperty("properties", sym.Properties.Select(p => p.Name).ToList());
        if (sym.Methods.Count > 0) node.SetProperty("methods", sym.Methods.Select(m => m.Name).ToList());
        if (sym.InjectedServices.Count > 0) node.SetProperty("injectedServices", sym.InjectedServices);

        _graph.AddNode(node);
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
            if (method.IsAbstract) node.SetProperty("isAbstract", true);
            node.SetProperty("isPublic", method.IsPublic);

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
        // Distinct because a caller invoking the same method three times is one
        // dependency, and the graph is read as navigation rather than as a profile.
        foreach (var (fromId, toId) in _roslyn.ExtractCallEdges().Distinct())
        {
            // Calls into types the classifier skipped have no node to point at.
            if (!_graph.HasNode(fromId) || !_graph.HasNode(toId)) continue;

            _graph.AddEdge(new GraphEdge
            {
                FromId = fromId,
                ToId = toId,
                Type = EdgeType.Calls
            });
        }
    }

    /// <summary>
    /// Adds JavaScript/CSS nodes and the three edges that cross the server/client
    /// boundary: which page loads an asset, which server-rendered data-* keys a
    /// script reads back, and which API routes a script calls.
    /// </summary>
    private void AddClientAssets(string projectDir, List<RazorPageInfo> razorInfos, string? idScope)
    {
        var assets = new ClientAssetExtractor().ExtractAssets(projectDir, idScope);

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
            if (asset.DataKeys.Count > 0) node.SetProperty("dataKeys", asset.DataKeys.OrderBy(k => k).ToList());
            if (asset.DataKeysWritten.Count > 0)
                node.SetProperty("dataKeysWritten", asset.DataKeysWritten.OrderBy(k => k).ToList());
            if (asset.ApiCalls.Count > 0) node.SetProperty("apiCalls", asset.ApiCalls.OrderBy(u => u).ToList());

            _graph.AddNode(node);
            byRelativePath[asset.RelativePath] = asset;
        }

        var byId = razorInfos.ToDictionary(r => r.Id, r => r);
        // Which pages reference each script, so an unread data key can be
        // distinguished from one no page ever renders.
        var renderedKeysByAsset = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

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

                var shared = asset.DataKeys.Intersect(scope.ServerBound, StringComparer.OrdinalIgnoreCase)
                    .OrderBy(k => k).ToList();
                if (shared.Count == 0) continue;

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
        }

        AnnotateUnboundKeys(assets, renderedKeysByAsset);
        AddJsToApiEdges(assets);
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

        while (pending.Count > 0)
        {
            var current = pending.Dequeue();
            // Partials can render each other; visited keeps a cycle from hanging the build.
            if (!visited.Add(current.Id)) continue;

            serverBound.UnionWith(current.ServerDataKeys);
            rendered.UnionWith(current.RenderedDataKeys);

            foreach (var partial in current.Partials)
            {
                var resolved = ResolvePartial(partial, allPages);
                if (resolved != null) pending.Enqueue(resolved);
            }
        }

        return new DataKeyScope(serverBound, rendered);
    }

    /// <summary>
    /// The data-* keys visible to a script on one composed page. ServerBound
    /// drives the coupling edge (server state reaching the client); Rendered
    /// drives unbound-key detection (does the attribute exist at all).
    /// </summary>
    private readonly record struct DataKeyScope(HashSet<string> ServerBound, HashSet<string> Rendered);

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
    /// Reachability is followed through Calls rather than stopping at direct
    /// invocations, because a test that goes through a builder or a fixture
    /// helper still covers what that helper drives. The depth it was reached at
    /// rides on the edge, so a consumer can ask for direct exercise alone; edges
    /// are only emitted across a project boundary, since a test calling its own
    /// helpers is not coverage of anything.
    /// </summary>
    private void AddCoverageEdges(int maxDepth = 3)
    {
        var tests = _graph.NodesOfType(NodeType.Method)
            .Where(n => n.GetProperty<bool>("isTest"))
            .ToList();
        if (tests.Count == 0) return;

        var callsOnly = new HashSet<EdgeType> { EdgeType.Calls };

        foreach (var test in tests)
        {
            var testProject = test.GetProperty<string>("project");

            // Materialised before the loop body runs: Traverse streams straight off
            // the adjacency lists, and adding a Covers edge below mutates one of
            // them mid-enumeration.
            var reached = _graph.Traverse(test.Id, callsOnly, maxDepth).ToList();

            foreach (var (node, _, depth) in reached)
            {
                if (node.Type != NodeType.Method) continue;
                if (node.GetProperty<bool>("isTest")) continue;

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
