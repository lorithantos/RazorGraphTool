namespace RazorGraph.Lua.Hosts;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Checks;

/// <summary>
/// Files sitting in a plug-in that nothing loads.
///
/// Lightroom only ever runs what the manifest names — menu items, the init and
/// shutdown hooks, the provider entries — plus whatever those files pull in with
/// require. Anything else in the folder ships with the plug-in and never
/// executes.
///
/// That is worth saying because the failure is one of BELIEF rather than
/// behaviour: nothing breaks, and someone edits the wrong file for months. It
/// happened here. A tree carried three copies of one script; the manifest named
/// one of them, the author was maintaining another, and the two had drifted nine
/// months apart. No error, no symptom, and no way to notice from the source.
///
/// Reachability, not name-matching, is the reason this belongs in a graph tool.
/// A file the manifest never mentions is still live if a file the manifest DOES
/// mention requires it, and only the require edges can settle that.
/// </summary>
public sealed class LightroomReachabilityRule : ILuaRule
{
    private const string ManifestFile = "Info.lua";

    public string Id => "lightroom.unreachable-file";

    public string Title => "Files in a plug-in that the manifest never loads";

    /// <summary>
    /// Manifest keys whose STRING value names a script, as against the nested
    /// <c>file =</c> entries the extractor already collects. From the catalogued
    /// vocabulary: the lifecycle hooks and the provider entries.
    /// </summary>
    private static readonly string[] FileValuedKeys =
    [
        "LrInitPlugin", "LrShutdownPlugin", "LrShutdownApp", "LrEnablePlugin", "LrDisablePlugin",
        "LrPluginInfoProvider", "LrExportServiceProvider", "LrExportFilterProvider",
        "LrMetadataProvider", "LrMetadataTagsetFactory", "LrHttpHandler", "LrWebGalleryProvider"
    ];

    public IEnumerable<LuaFinding> Check(LuaCheckContext context)
    {
        foreach (var manifest in context.Declarations.Where(IsManifest))
        {
            var plugin = FolderOf(manifest.File.RelativePath);

            // Everything graphed under this plug-in, keyed by file stem.
            var inPlugin = context.Declarations
                .Where(d => FolderOf(d.File.RelativePath) == plugin && !IsManifest(d))
                .ToList();

            if (inPlugin.Count == 0) continue;

            var roots = RootsOf(manifest);
            if (roots.Count == 0)
            {
                // A manifest naming no scripts at all is either a data-only
                // plug-in or something this reader could not follow. Either way
                // every file would look unreachable, and reporting all of them
                // would be a rule failing loudly at the user instead of at itself.
                continue;
            }

            var reached = Reachable(context, plugin, roots);

            foreach (var orphan in inPlugin
                .Where(d => !reached.Contains(StemOf(d.File.RelativePath), StringComparer.OrdinalIgnoreCase))
                .OrderBy(d => d.File.RelativePath, StringComparer.Ordinal))
            {
                yield return new LuaFinding(
                    Id, LuaSeverity.Note, orphan.File.RelativePath, 0,
                    "ships in the plug-in but nothing loads it: not named in Info.lua and not required by anything that is",
                    $"manifest entry points: {string.Join(", ", roots.OrderBy(r => r, StringComparer.Ordinal))}");
            }
        }
    }

    /// <summary>
    /// Stems the manifest names directly: nested <c>file =</c> entries plus the
    /// string-valued lifecycle and provider keys.
    /// </summary>
    private static HashSet<string> RootsOf(LuaFileDeclarations manifest)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var reference in manifest.ReturnedFileReferences) roots.Add(StemOf(reference));

        foreach (var field in manifest.ReturnedFields ?? [])
        {
            if (field.Kind != "string" || field.Value is not { Length: > 0 } value) continue;
            if (!FileValuedKeys.Contains(field.Key, StringComparer.Ordinal)) continue;
            roots.Add(StemOf(value));
        }

        return roots;
    }

    /// <summary>
    /// Stems reachable from the manifest's entry points, following require/import
    /// edges inside this plug-in.
    ///
    /// The edges come from the graph rather than from re-reading source: that is
    /// the whole reason resolution happened first, and it is what separates "not
    /// mentioned" from "not reachable".
    /// </summary>
    private static HashSet<string> Reachable(LuaCheckContext context, string plugin, HashSet<string> roots)
    {
        var moduleByStem = new Dictionary<string, GraphNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in context.Graph.Nodes.Where(n => n.ForeignType == LuaGraphBuilder.ModuleKind))
        {
            var path = node.GetProperty<string>("relativePath");
            if (path is null || FolderOf(path) != plugin) continue;
            moduleByStem.TryAdd(StemOf(path), node);
        }

        var reached = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>(roots);

        while (queue.Count > 0)
        {
            var stem = queue.Dequeue();
            if (!reached.Add(stem)) continue;
            if (!moduleByStem.TryGetValue(stem, out var module)) continue;

            foreach (var edge in context.Graph.Edges.Where(e =>
                e.ForeignType == LuaGraphBuilder.RequiresKind && e.FromId == module.Id))
            {
                var target = context.Graph.GetNode(edge.ToId)?.GetProperty<string>("relativePath");
                if (target is not null) queue.Enqueue(StemOf(target));
            }
        }

        return reached;
    }

    private static bool IsManifest(LuaFileDeclarations declaration) =>
        string.Equals(Path.GetFileName(declaration.File.RelativePath), ManifestFile, StringComparison.OrdinalIgnoreCase);

    private static string FolderOf(string relativePath) =>
        (Path.GetDirectoryName(relativePath) ?? "").Replace('\\', '/');

    /// <summary>File name without extension: manifests write both "X" and "X.lua".</summary>
    private static string StemOf(string path) => Path.GetFileNameWithoutExtension(path);
}
