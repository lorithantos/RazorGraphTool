namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Client;
using RazorGraph.Extractor.Razor;

/// <summary>
/// Emits the client tier: JavaScript/CSS nodes (files and inline blocks
/// alike), the edges that cross the server/client boundary, and the
/// unbound-key and unbound-selector annotations. Owns the vendor-skip
/// summaries a build reports.
/// </summary>
internal sealed class ClientAssetEmitter(CodeGraph graph)
{
    /// <summary>One line per project whose client-asset scan dropped vendor files.</summary>
    internal IReadOnlyList<string> AssetSkipSummaries => _assetSkipSummaries;
    private readonly List<string> _assetSkipSummaries = new();

    /// <summary>
    /// Adds JavaScript/CSS nodes and the three edges that cross the server/client
    /// boundary: which page loads an asset, which server-rendered data-* keys a
    /// script reads back, and which API routes a script calls.
    /// </summary>
    internal void AddClientAssets(
        string projectDir, List<RazorPageInfo> razorInfos, string? idScope, bool includeVendorAssets)
    {
        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(projectDir, idScope, includeVendorAssets);
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

            graph.AddNode(node);
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
                graph.AddEdge(new GraphEdge
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
                    graph.AddEdge(new GraphEdge
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
                    graph.AddEdge(new GraphEdge
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
                var resolved = RazorLayerEmitter.ResolvePartial(partial, allPages);
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

            graph.GetNode(asset.Id)?.SetProperty("unboundDataKeys", unbound);
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

            graph.GetNode(asset.Id)?.SetProperty("unboundSelectorIds", unbound);
        }
    }

    /// <summary>
    /// Bind literal fetch URLs to the controller that serves them, matching
    /// /api/{controller}/... against the ApiController nodes already in the graph.
    /// </summary>
    private void AddJsToApiEdges(List<ClientAssetInfo> assets)
    {
        var controllers = graph.NodesOfType(NodeType.ApiController).ToList();
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

                graph.AddEdge(new GraphEdge
                {
                    FromId = asset.Id,
                    ToId = controller.Id,
                    Type = EdgeType.Calls,
                    Properties = { ["url"] = url }
                });
            }
        }
    }
}
