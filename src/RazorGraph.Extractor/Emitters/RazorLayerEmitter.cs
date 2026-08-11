namespace RazorGraph.Extractor;

using Microsoft.AspNetCore.Razor.Language;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Razor;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Binding;
using RazorGraph.Extractor.Razor;
using RazorGraph.Extractor.Roslyn;
using SymbolInfo = RazorGraph.Extractor.Roslyn.SymbolInfo;

/// <summary>
/// Emits the Razor layer of one project: page and partial nodes, their
/// correlation to the Roslyn symbols already in the graph, partial render
/// edges, the page→compiled-class join, and — via the client-asset emitter —
/// the client tier those pages load.
/// </summary>
internal sealed class RazorLayerEmitter(CodeGraph graph, ClientAssetEmitter clientAssets)
{
    /// <summary>
    /// Razor files, their correlation to the symbols already in the graph,
    /// partial cross-references, and client assets, for one project.
    /// </summary>
    internal void BuildRazorLayer(
        string projectDir, string? idScope, Compilation? compilation,
        List<SymbolInfo> symbols, bool includeVendorAssets)
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
            CorrelateRazorToRoslyn(info, symbols, idScope);
        }

        foreach (var info in razorInfos)
        {
            AddPartialEdges(info, razorInfos);
        }

        clientAssets.AddClientAssets(projectDir, razorInfos, idScope, includeVendorAssets);
    }

    /// <summary>
    /// Best-effort: discover tag helper descriptors from the loaded compilation so the
    /// Razor parser can bind tag helper elements. Failure is non-fatal — the extractor's
    /// text scan still captures asp-* attributes.
    /// </summary>
    private static void TryProvideTagHelpers(RazorExtractor razorExtractor, Compilation? compilation)
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

    /// <summary>
    /// Wire each Razor node to the class its file was compiled into, joining
    /// the Razor layer and the Roslyn layer views of the same artifact. The
    /// edge reuses References with compiledInto=true rather than a new
    /// EdgeType: the serializer writes enum names as strings, so a new member
    /// would make every new graph unreadable by older deserializers.
    /// </summary>
    internal void AddGeneratedClassLinks(IReadOnlyList<SymbolInfo> symbols)
    {
        var razorByPath = graph.Nodes
            .Where(n => n.Type is NodeType.RazorPage or NodeType.PartialView or NodeType.Layout)
            .Where(n => n.FilePath != null)
            .GroupBy(n => NormalizeFullPath(n.FilePath!))
            .ToDictionary(g => g.Key, g => g.ToList());
        if (razorByPath.Count == 0) return;

        foreach (var sym in symbols)
        {
            if (sym.GeneratedFrom == null) continue;
            if (!razorByPath.TryGetValue(NormalizeFullPath(sym.GeneratedFrom), out var razorNodes)) continue;

            foreach (var razorNode in razorNodes)
            {
                graph.AddEdge(new GraphEdge
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

        graph.AddNode(node);
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
                graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = modelNode.Id,
                    Type = EdgeType.PageServedBy
                });

                // Link PageModel → page (bidirectional for queries)
                graph.AddEdge(new GraphEdge
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
            var vmNode = graph.Nodes.FirstOrDefault(n =>
                (n.Type == NodeType.ViewModel || n.Type == NodeType.Class) &&
                (n.GetProperty<string>("fullName") == info.ModelType || n.Name == info.ModelType));

            if (vmNode != null)
            {
                graph.AddEdge(new GraphEdge
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
            if (graph.HasNode(layoutId))
            {
                graph.AddEdge(new GraphEdge
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
                graph.AddNode(thNode);

                graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = thNodeId,
                    Type = EdgeType.RendersComponent
                });

                // Try to bind to ViewModel property
                if (info.ModelType != null)
                {
                    var vmNode = graph.Nodes.FirstOrDefault(n =>
                        n.Name == info.ModelType || n.GetProperty<string>("fullName") == info.ModelType);

                    if (vmNode != null)
                    {
                        graph.AddEdge(new GraphEdge
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
            var partialFile = ResolvePartial(partial, allPages, info.RelativePath);

            if (partialFile != null)
            {
                graph.AddEdge(new GraphEdge
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
                if (!graph.HasNode(stubId))
                {
                    graph.AddNode(new GraphNode
                    {
                        Id = stubId,
                        Type = NodeType.PartialView,
                        Name = partial.Name
                    });
                }
                graph.AddEdge(new GraphEdge
                {
                    FromId = info.Id,
                    ToId = stubId,
                    Type = EdgeType.RendersPartial
                });
            }
        }
    }

    /// <summary>
    /// The parsed page whose path contains a partial's name. Shared with the
    /// client-asset emitter, whose composed-DOM scope walks the same render
    /// relation.
    /// </summary>
    /// <summary>
    /// The template a partial name refers to, or null when nothing can serve it.
    ///
    /// Delegates to <see cref="ViewNameResolver"/>, which searches ASP.NET's
    /// folders by path SEGMENT. The previous rule here was a substring match over
    /// the whole relative path taking the first hit, which on OrchardCore's 1,576
    /// templates matched "Menu" against 71 paths and chose a _ViewImports.cshtml.
    /// </summary>
    internal static RazorPageInfo? ResolvePartial(PartialRenderInfo partial, List<RazorPageInfo> allPages) =>
        ResolvePartial(partial, allPages, fromRelativePath: null);

    /// <inheritdoc cref="ResolvePartial(PartialRenderInfo, List{RazorPageInfo})"/>
    /// <param name="fromRelativePath">
    /// The referencing file, so a partial sitting beside its caller wins over a
    /// same-named file elsewhere in the solution.
    /// </param>
    internal static RazorPageInfo? ResolvePartial(
        PartialRenderInfo partial, List<RazorPageInfo> allPages, string? fromRelativePath)
    {
        var byPath = new Dictionary<string, RazorPageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in allPages)
        {
            var key = page.RelativePath.Replace('\\', '/').TrimStart('/');
            // First writer wins: duplicate relative paths across projects are the
            // ambiguity the resolver reports, not something to overwrite silently.
            if (!byPath.ContainsKey(key)) byPath[key] = page;
        }

        var best = ViewNameResolver.ResolveOne(partial.Name, fromRelativePath, byPath.Keys);
        return best is not null && byPath.TryGetValue(best, out var resolved) ? resolved : null;
    }
}
