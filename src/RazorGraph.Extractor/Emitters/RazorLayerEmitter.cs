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
    /// Razor files awaiting binding, with the project each came from. Resolution
    /// is deferred because a view name resolves against the referencing project
    /// AND every project it references — a partial living in a Razor Class
    /// Library is a legitimate target that a per-project pass cannot see — and
    /// the project nodes carrying that structure do not exist until the last
    /// project has been loaded.
    /// </summary>
    private readonly List<(RazorPageInfo Info, string? Project)> _pendingBindings = new();

    /// <summary>
    /// placement.json rules awaiting the same post-pass, for the same reason: a
    /// name a rule introduces resolves against every project the module can see.
    /// </summary>
    private readonly List<(PlacementEntry Entry, string? Project, string RelativePath)> _pendingPlacements = new();

    /// <summary>
    /// What is known about each name, accumulated across code and config before
    /// anything is resolved.
    ///
    /// Two producers of the same name must agree on one node, and the finding
    /// depends on facts that arrive in no fixed order — a driver naming a shape,
    /// a placement rule hiding it — so binding is decided in a sweep at the end
    /// rather than at whichever mention happens to come first.
    /// </summary>
    private sealed class NameFacts
    {
        public required string NodeId { get; init; }

        /// <summary>Projects that mention the name; candidates must be visible from one of them.</summary>
        public HashSet<string?> Projects { get; } = new();

        /// <summary>
        /// Mentions that REQUIRE a template, described for the report. An alternate
        /// does not qualify: OrchardCore tries alternates and falls back to the
        /// base shape, so a missing alternate template is the mechanism working,
        /// not a failure.
        /// </summary>
        public List<string> RequiredBy { get; } = new();

        /// <summary>Set when a placement rule drops the shape on every render.</summary>
        public string? SuppressedBy { get; set; }
    }

    private readonly Dictionary<string, NameFacts> _names = new(StringComparer.Ordinal);

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

        // Held for the binding post-pass rather than resolved here. Partial names
        // resolve against the referencing project AND the projects it references,
        // and project nodes do not exist until every project has been loaded —
        // the same reason GraphBuilder accumulates symbols instead of correlating
        // Razor per project.
        foreach (var info in razorInfos) _pendingBindings.Add((info, idScope));

        CollectPlacementFiles(projectDir, idScope);

        clientAssets.AddClientAssets(projectDir, razorInfos, idScope, includeVendorAssets);
    }

    /// <summary>
    /// placement.json files in one project, held for the binding post-pass.
    ///
    /// A malformed one is reported and skipped rather than failing the build,
    /// matching how unparseable Razor is handled: config that cannot be read is a
    /// gap in the report, not a reason to have no graph.
    /// </summary>
    private void CollectPlacementFiles(string projectDir, string? idScope)
    {
        // Matched by exact name, which is what OrchardCore looks for. Case
        // sensitivity follows the platform, and the framework ships the file
        // lower-case, so the lower-case spelling is the only one that resolves on
        // Linux either.
        foreach (var file in Directory.EnumerateFiles(projectDir, PlacementReader.FileName, SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                              && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")))
        {
            try
            {
                var relative = Path.GetRelativePath(projectDir, file).Replace('\\', '/');
                foreach (var entry in PlacementReader.Read(file))
                {
                    _pendingPlacements.Add((entry, idScope, relative));
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: failed to read {file}: {ex.Message}");
            }
        }
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

    /// <summary>
    /// Resolve every deferred view name and emit its edges, once all projects are
    /// in the graph.
    ///
    /// Candidates come from the graph rather than a directory walk: the
    /// referencing project first, then the projects it references over DependsOn.
    /// That prunes unrelated same-named files at source and makes Razor Class
    /// Library views resolvable, which a filesystem scan of one project directory
    /// can never do.
    ///
    /// A single-project build has no Project nodes and no project attribution at
    /// all, because idScope is null. Everything in the graph is then the one
    /// project, so scoping is skipped rather than narrowed to nothing.
    /// </summary>
    internal void AddBindingEdges(
        IReadOnlyList<ViewCall>? viewCalls = null,
        IReadOnlyList<ShapeReference>? shapeNames = null,
        IReadOnlyList<ActionMethod>? actionMethods = null)
    {
        var visibleProjects = BuildProjectVisibility();

        foreach (var (info, project) in _pendingBindings)
        {
            var candidates = _pendingBindings
                .Where(c => IsVisible(project, c.Project, visibleProjects))
                .Select(c => c.Info)
                .ToList();

            AddPartialEdges(info, candidates);
        }

        var actionNames = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var action in actionMethods ?? []) actionNames.TryAdd(action.MethodId, action.ActionName);

        foreach (var call in viewCalls ?? [])
        {
            AddViewCallEdge(call, visibleProjects, actionNames);
        }

        // Names first, from both sources, then resolution. Code and config each
        // name shapes the other never mentions, and a placement rule can retire a
        // finding a driver raises — so nothing is judged until every mention is in.
        foreach (var shape in shapeNames ?? []) AddShapeReference(shape);
        foreach (var (entry, project, relativePath) in _pendingPlacements) AddPlacementRule(entry, project, relativePath);

        BindNames(visibleProjects);
    }

    /// <summary>
    /// Record one name a driver produces, and whether anything must serve it.
    /// </summary>
    private void AddShapeReference(ShapeReference shape)
    {
        var producer = graph.GetNode(shape.MethodId);
        var project = producer?.GetProperty<string>("project");
        var facts = EnsureName(shape.Name, project);

        if (producer is not null)
        {
            var produces = new GraphEdge
            {
                FromId = producer.Id,
                ToId = facts.NodeId,
                Type = EdgeType.Produces
            };
            produces.Properties["line"] = shape.Line;
            if (shape.IsAlternate) produces.Properties["isAlternate"] = true;
            if (shape.InTestCode) produces.Properties["inTestCode"] = true;
            graph.AddEdge(produces);
        }

        // Neither of these can produce a runtime failure for want of a template:
        // an alternate is tried and fallen back from, and test code renders
        // nothing. Both stay in the graph — they are real mentions — and both stay
        // out of the report.
        if (shape.IsAlternate || shape.InTestCode) return;

        facts.RequiredBy.Add(producer is null ? "unknown producer" : $"{producer.Name}:{shape.Line}");
    }

    /// <summary>
    /// Apply one placement.json rule: the shape it places, the names it adds, and
    /// the hide that can prove a shape never renders at all.
    /// </summary>
    private void AddPlacementRule(PlacementEntry entry, string? project, string relativePath)
    {
        var configId = EnsurePlacementNode(entry, project, relativePath);

        // The key is a REFERENCE, not a producer. Arranging a shape another module
        // owns is ordinary here — OrchardCore.Contents places parts it does not
        // define — so demanding a template for every placed name would invent a
        // finding for every module that lays out someone else's content.
        var placed = EnsureName(entry.ShapeType, project);
        var reference = new GraphEdge
        {
            FromId = configId,
            ToId = placed.NodeId,
            Type = EdgeType.References
        };
        reference.Properties["role"] = "placement";
        reference.Properties["line"] = entry.Line;
        if (entry.Place is { Length: > 0 } place) reference.Properties["place"] = place;
        if (entry.DisplayType is { Length: > 0 } displayType) reference.Properties["displayType"] = displayType;
        if (entry.Differentiator is { Length: > 0 } differentiator) reference.Properties["differentiator"] = differentiator;
        if (entry.Filters.Count > 0) reference.Properties["filters"] = entry.Filters.ToList();
        if (entry.Hides) reference.Properties["hides"] = true;
        graph.AddEdge(reference);

        // Only an UNCONDITIONAL hide proves the shape never renders. A hide behind
        // a content type or a display type still leaves every other path live, so
        // it must not retire the finding.
        if (entry.Hides && entry.IsUnconditional)
        {
            placed.SuppressedBy = $"{relativePath}:{entry.Line} places it as \"-\"";
        }

        foreach (var alternate in entry.Alternates) AddPlacementName(configId, alternate, project, entry, "alternate", required: false);
        foreach (var wrapper in entry.Wrappers) AddPlacementName(configId, wrapper, project, entry, "wrapper", required: true);

        if (entry.RenamedTo is { Length: > 0 } renamed)
        {
            AddPlacementName(configId, renamed, project, entry, "shape", required: true);
            graph.GetNode(placed.NodeId)?.SetProperty("renamedTo", renamed);
        }
    }

    /// <summary>
    /// A name a placement rule introduces.
    /// </summary>
    /// <param name="required">
    /// Whether a missing template is a failure. A wrapper and a substituted shape
    /// are rendered as shapes in their own right and throw when nothing binds; an
    /// alternate is an optional override with the base shape behind it.
    /// </param>
    private void AddPlacementName(
        string configId, string name, string? project, PlacementEntry entry, string role, bool required)
    {
        var facts = EnsureName(name, project);

        var edge = new GraphEdge
        {
            FromId = configId,
            ToId = facts.NodeId,
            Type = EdgeType.Produces
        };
        edge.Properties["role"] = role;
        edge.Properties["line"] = entry.Line;
        graph.AddEdge(edge);

        if (required) facts.RequiredBy.Add($"{Path.GetFileName(entry.FilePath)}:{entry.Line} ({role})");
    }

    private string EnsurePlacementNode(PlacementEntry entry, string? project, string relativePath)
    {
        var id = project is null ? $"placement:{relativePath}" : $"placement:{project}:{relativePath}";
        if (graph.HasNode(id)) return id;

        var node = new GraphNode
        {
            Id = id,
            Type = NodeType.ConfigurationFile,
            Name = relativePath,
            FilePath = entry.FilePath
        };
        node.SetProperty("kind", "orchardCorePlacement");
        if (project is not null) node.SetProperty("project", project);
        graph.AddNode(node);

        return id;
    }

    private NameFacts EnsureName(string name, string? project)
    {
        if (!_names.TryGetValue(name, out var facts))
        {
            var id = $"shape:{name}";
            _names[name] = facts = new NameFacts { NodeId = id };

            if (!graph.HasNode(id))
            {
                var node = new GraphNode
                {
                    Id = id,
                    Type = NodeType.NamedBinding,
                    Name = name
                };
                node.SetProperty("kind", "orchardCoreShape");
                graph.AddNode(node);
            }
        }

        facts.Projects.Add(project);
        return facts;
    }

    /// <summary>
    /// Hang every template that could serve each name off the name, then report
    /// the names nothing serves.
    ///
    /// Every visible match is emitted rather than the first, because that is the
    /// mechanism OrchardCore is built on: a theme serving Menu-Main.cshtml
    /// overrides a module's without either one changing, and a graph showing only
    /// one of them hides the override it exists to reveal. Rank carries the order.
    ///
    /// A name with NO candidate is the finding this pass exists for — but only
    /// when something actually requires a template, and only when no placement
    /// rule drops the shape entirely.
    /// </summary>
    private void BindNames(Dictionary<string, HashSet<string>> visibleProjects)
    {
        var byStem = new Dictionary<string, List<(RazorPageInfo Info, string? Project)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pending in _pendingBindings)
        {
            var stem = Path.GetFileNameWithoutExtension(pending.Info.RelativePath);
            if (!byStem.TryGetValue(stem, out var templates)) byStem[stem] = templates = new();
            templates.Add(pending);
        }

        // Ordinal name order, so two runs of the same solution produce the same
        // file and a diff of two graphs shows only what changed.
        foreach (var (name, facts) in _names.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            var node = graph.GetNode(facts.NodeId);
            if (node is null) continue;

            var rank = 0;
            foreach (var candidate in ShapeNameGrammar.CandidateTemplateNames(name))
            {
                if (!byStem.TryGetValue(candidate, out var templates)) continue;

                foreach (var (template, templateProject) in templates.OrderBy(t => t.Info.Id, StringComparer.Ordinal))
                {
                    if (!facts.Projects.Any(from => IsVisible(from, templateProject, visibleProjects))) continue;

                    var bound = new GraphEdge
                    {
                        FromId = facts.NodeId,
                        ToId = template.Id,
                        Type = EdgeType.BoundBy
                    };
                    bound.Properties["rank"] = rank++;
                    bound.Properties["bindingKind"] =
                        Path.GetExtension(template.RelativePath).Equals(".liquid", StringComparison.OrdinalIgnoreCase)
                            ? "liquid"
                            : "razor";
                    bound.Properties["matchedAs"] = candidate;
                    graph.AddEdge(bound);
                }
            }

            node.SetProperty("bound", rank > 0);
            if (rank > 0) continue;

            node.SetProperty("unbound", true);

            if (facts.RequiredBy.Count == 0)
            {
                // Mentioned only where a miss is survivable: an alternate, a
                // placement key, a test fixture. Kept on the node so a query can
                // still reach it, kept out of the report so the report stays worth
                // reading — a finding stream with noise in it gets ignored whole.
                node.SetProperty("noRequiredProducer", true);
                continue;
            }

            if (facts.SuppressedBy is { } suppressor)
            {
                node.SetProperty("renderSuppressedBy", suppressor);
                continue;
            }

            node.SetProperty("requiredBy", facts.RequiredBy.ToList());
            _unboundShapes.Add($"{name} (produced by {string.Join(", ", facts.RequiredBy)})");
        }
    }

    /// <summary>
    /// Shape names nothing binds, collected during the binding pass. OrchardCore
    /// throws when a shape resolves to no binding — no catch-all rendering — so
    /// each of these is a runtime failure on whatever path produces it. Held here
    /// so every build surface can report them: a finding that only exists for
    /// someone who thinks to query for it is not a finding, it is a secret.
    /// </summary>
    internal IReadOnlyList<string> UnboundShapes => _unboundShapes;
    private readonly List<string> _unboundShapes = new();

    /// <summary>
    /// Link a controller action to the view it renders.
    ///
    /// Reuses ReturnsView, which already means "this code renders that view" for
    /// the Razor Pages pairing — an action returning a view is the same relation.
    /// A dynamic name records itself on the action rather than guessing a target,
    /// because inventing an edge here would be worse than having none.
    /// </summary>
    private void AddViewCallEdge(
        ViewCall call, Dictionary<string, HashSet<string>> visibleProjects, Dictionary<string, string> actionNames)
    {
        var action = graph.GetNode(call.MethodId);
        if (action is null) return;

        if (call.Source == ViewNameSource.InvokingAction)
        {
            AddHelperRenderEdges(call, action, visibleProjects, actionNames);
            return;
        }

        if (call.Name is null)
        {
            var unresolved = action.GetProperty<List<string>>("unresolvedViews") ?? [];
            unresolved.Add($"line {call.Line}: {call.Reason}");
            action.SetProperty("unresolvedViews", unresolved);
            return;
        }

        var view = ResolveView(call.Name, call.Controller, action.GetProperty<string>("project"), visibleProjects);
        if (view is null)
        {
            // Named a view that no template serves. Recorded on the action: at
            // runtime this is a 500, and it is exactly what this pass is for.
            var missing = action.GetProperty<List<string>>("missingViews") ?? [];
            missing.Add($"line {call.Line}: {call.Name}");
            action.SetProperty("missingViews", missing);
            return;
        }

        var edge = new GraphEdge
        {
            FromId = call.MethodId,
            ToId = view.Id,
            Type = EdgeType.ReturnsView
        };
        edge.Properties["viewName"] = call.Name;
        edge.Properties["nameSource"] = call.Source.ToString();
        edge.Properties["line"] = call.Line;
        graph.AddEdge(edge);
    }

    /// <summary>
    /// Attribute a helper's render to the actions that invoke it.
    ///
    /// A private method ending in <c>return View(model)</c> renders the INVOKING
    /// action's view, taken from route data — so the helper has no name of its
    /// own, and its own name is the one answer guaranteed to be wrong. What the
    /// helper does have is callers, and the graph already knows them: the edge
    /// runs from each calling action to the view it thereby renders, which is
    /// also the direction a reader asks the question in ("what does this action
    /// render?").
    ///
    /// One hop only. A helper reached through another helper is left unresolved
    /// rather than followed, because the chain can fan out to actions that never
    /// reach this call and a wrong edge is worse than a missing one.
    /// </summary>
    private void AddHelperRenderEdges(
        ViewCall call, GraphNode helper,
        Dictionary<string, HashSet<string>> visibleProjects, Dictionary<string, string> actionNames)
    {
        var callers = CallersOf().TryGetValue(call.MethodId, out var found) ? found : [];
        var rendered = new List<string>();

        foreach (var callerId in callers.OrderBy(id => id, StringComparer.Ordinal))
        {
            if (!actionNames.TryGetValue(callerId, out var actionName)) continue;

            var caller = graph.GetNode(callerId);
            if (caller is null) continue;

            var view = ResolveView(actionName, call.Controller, caller.GetProperty<string>("project"), visibleProjects);
            if (view is null)
            {
                var missing = caller.GetProperty<List<string>>("missingViews") ?? [];
                missing.Add($"line {call.Line} (via {helper.Name}): {actionName}");
                caller.SetProperty("missingViews", missing);
                continue;
            }

            var edge = new GraphEdge
            {
                FromId = callerId,
                ToId = view.Id,
                Type = EdgeType.ReturnsView
            };
            edge.Properties["viewName"] = actionName;
            edge.Properties["nameSource"] = call.Source.ToString();
            edge.Properties["via"] = helper.Name;
            edge.Properties["line"] = call.Line;
            graph.AddEdge(edge);

            rendered.Add($"{actionName} (for {caller.Name})");
        }

        if (rendered.Count > 0)
        {
            helper.SetProperty("rendersForCallers", rendered);
            return;
        }

        // No action caller found: unreached, called only through another helper,
        // or invoked by something outside this graph. Reported as unresolved, the
        // same as any other name that could not be worked out.
        var unresolved = helper.GetProperty<List<string>>("unresolvedViews") ?? [];
        unresolved.Add($"line {call.Line}: {call.Reason}");
        helper.SetProperty("unresolvedViews", unresolved);
    }

    /// <summary>
    /// The template a controller renders for a view name, searched over the
    /// projects it can see.
    /// </summary>
    private RazorPageInfo? ResolveView(
        string name, string controller, string? project, Dictionary<string, HashSet<string>> visibleProjects)
    {
        var byPath = new Dictionary<string, RazorPageInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var (info, candidateProject) in _pendingBindings)
        {
            if (!IsVisible(project, candidateProject, visibleProjects)) continue;

            var key = info.RelativePath.Replace('\\', '/').TrimStart('/');
            if (!byPath.ContainsKey(key)) byPath[key] = info;
        }

        var best = ViewNameResolver.ResolveForController(name, controller, byPath.Keys).FirstOrDefault();
        return best is not null && byPath.TryGetValue(best, out var view) ? view : null;
    }

    /// <summary>
    /// Who calls each method, indexed once. Built from the Calls edges already in
    /// the graph, which is the whole reason this resolution is possible without a
    /// second Roslyn pass.
    /// </summary>
    private Dictionary<string, List<string>> CallersOf()
    {
        if (_callersOf is not null) return _callersOf;

        _callersOf = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.Calls))
        {
            if (!_callersOf.TryGetValue(edge.ToId, out var callers)) _callersOf[edge.ToId] = callers = new();
            if (!callers.Contains(edge.FromId)) callers.Add(edge.FromId);
        }

        return _callersOf;
    }

    private Dictionary<string, List<string>>? _callersOf;

    /// <summary>
    /// Each project mapped to the projects it can see: itself plus everything it
    /// references. Empty when the graph carries no project structure.
    /// </summary>
    private Dictionary<string, HashSet<string>> BuildProjectVisibility()
    {
        var visibility = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var node in graph.NodesOfType(NodeType.Project))
        {
            visibility[node.Name] = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { node.Name };
        }

        foreach (var edge in graph.Edges.Where(e => e.Type == EdgeType.DependsOn))
        {
            // DependsOn runs referencing -> referenced, so the target is what the
            // source can additionally see.
            var from = graph.GetNode(edge.FromId);
            var to = graph.GetNode(edge.ToId);
            if (from is null || to is null) continue;
            if (visibility.TryGetValue(from.Name, out var seen)) seen.Add(to.Name);
        }

        return visibility;
    }

    private static bool IsVisible(string? fromProject, string? candidateProject, Dictionary<string, HashSet<string>> visibility)
    {
        // No project structure: a single-project build, where every Razor file in
        // the graph belongs to the only project there is.
        if (fromProject is null || candidateProject is null || visibility.Count == 0) return true;

        return visibility.TryGetValue(fromProject, out var seen)
            ? seen.Contains(candidateProject)
            : string.Equals(fromProject, candidateProject, StringComparison.OrdinalIgnoreCase);
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
