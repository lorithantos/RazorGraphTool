namespace RazorGraph.Lua.Hosts;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.ExternalApis;

/// <summary>
/// Adobe Lightroom plugins: an <c>Info.lua</c> manifest beside a set of .lua files,
/// with <c>import</c> rather than <c>require</c>.
///
/// Measured on LR-Lua (2026-08-10): import appears 191 times against 22 require,
/// so a require-only extractor finds almost nothing here. Nearly all imports name
/// the Lightroom SDK — LrDialogs, LrView, LrFunctionContext — which lives in the
/// host application, not in the plugin. Those resolve to External: they are not
/// missing modules, they are someone else's, and reporting them as failures would
/// mean a healthy plugin looked broken 191 times over.
/// </summary>
public sealed class LightroomHost : ILuaHost
{
    private static readonly Lazy<ExternalApiCatalog> Sdk =
        new(() => ExternalApiCatalog.LoadEmbedded("lightroom-classic"));

    private readonly string _root;

    public LightroomHost(string root) => _root = Path.GetFullPath(root);

    /// <summary>The catalogued Lightroom Classic SDK surface used to classify imports.</summary>
    public static ExternalApiCatalog Catalog => Sdk.Value;

    public string Name => "lightroom";

    /// <summary>The Lightroom SDK is Lua 5.1.</summary>
    public LuaDialect Dialect => LuaDialect.Lua51;

    public ModuleReferenceSupport ReferenceSupport => ModuleReferenceSupport.Static;

    // Both, because plugins do use require for their own files -- 22 times in the
    // measured corpus -- even though import dominates for SDK modules.
    public IReadOnlySet<string> ReferenceFunctions { get; } =
        new HashSet<string>(StringComparer.Ordinal) { "import", "require" };

    public IEnumerable<LuaSourceFile> Discover(string rootPath) =>
        Directory.EnumerateFiles(rootPath, "*.lua", SearchOption.AllDirectories)
            .Select(f => new LuaSourceFile(f, Path.GetRelativePath(_root, f)))
            .OrderBy(f => f.RelativePath, StringComparer.Ordinal);

    /// <summary>
    /// Lightroom source addresses files by bare stem, but the stem is unique only
    /// within one plugin, and the plugin name is unique only within one tree —
    /// LR-Lua carries two SDK copies whose sample plugins share names, so even
    /// plugin-qualified ids collided 33 times out of 75 files. The relative path
    /// is the only name that is unique by construction.
    ///
    /// Verbose, and worth it: a colliding id silently merges unrelated code into
    /// one node, which is the confidently-wrong failure the whole exercise is
    /// trying not to ship. Resolution is unaffected — it matches on the stem the
    /// source actually writes, then maps the resulting path to this id.
    /// </summary>
    public string ModuleNameFor(LuaSourceFile file)
    {
        var rel = file.RelativePath.Replace('\\', '/');
        return rel.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) ? rel[..^4] : rel;
    }

    /// <summary>
    /// Record what the SDK catalogue makes of this module's imports: which are
    /// known Lightroom Classic modules, what minimum SDK version they imply, and
    /// which the catalogue does not recognise.
    ///
    /// Unrecognised is reported as unrecognised-by-this-catalogue, never as
    /// invalid. The catalogue stops well short of the newest SDK Adobe ships, so
    /// the likeliest reason for an unknown Lr* module is that it is newer than
    /// anything catalogued — calling it an error would be a confident wrong answer.
    /// </summary>
    public void Annotate(GraphNode module, IReadOnlyList<string> externalNames)
    {
        var sdkNames = externalNames.Where(IsSdkModule).ToList();
        if (sdkNames.Count == 0) return;

        var verdicts = sdkNames.Select(Catalog.Classify).ToList();

        var known = verdicts.OfType<ExternalApiVerdict.Known>().ToList();
        if (known.Count > 0)
        {
            module.SetProperty("sdkModules", known.Select(k => k.Name).ToList());

            // A module removed from the newest catalogued SDK is a live
            // compatibility problem, not trivia.
            var retired = known.Where(k => k.AbsentAfter is not null)
                .Select(k => $"{k.Name} (absent after SDK {k.AbsentAfter})").ToList();
            if (retired.Count > 0) module.SetProperty("sdkModulesRetired", retired);
        }

        if (Catalog.MinimumVersionFor(sdkNames) is { } minimum)
            module.SetProperty("minimumSdkVersion", minimum);

        var unknown = verdicts.OfType<ExternalApiVerdict.UnknownToCatalogue>().Select(u => u.Name).ToList();
        if (unknown.Count > 0)
        {
            module.SetProperty("sdkModulesUnknownToCatalogue", unknown);
            module.SetProperty("sdkCatalogueRange",
                $"catalogued {string.Join(", ", Catalog.CatalogedVersions)}; {Catalog.Product} has shipped through {Catalog.NewestKnownRelease}");
        }
    }

    /// <summary>An Lr-prefixed name, i.e. a candidate Lightroom SDK module.</summary>
    private static bool IsSdkModule(string name) =>
        name.StartsWith("Lr", StringComparison.Ordinal);

    /// <summary>The .lrdevplugin directory a file sits under, if any.</summary>
    private static string? PluginOf(string relativePath) =>
        relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .FirstOrDefault(segment => segment.EndsWith(".lrdevplugin", StringComparison.OrdinalIgnoreCase));

    /// <summary>Absolute path of the plugin directory containing a file, if any.</summary>
    private string? PluginRootOf(string fullPath)
    {
        var plugin = PluginOf(Path.GetRelativePath(_root, fullPath));
        if (plugin is null) return null;

        for (var dir = Path.GetDirectoryName(fullPath); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (string.Equals(Path.GetFileName(dir), plugin, StringComparison.OrdinalIgnoreCase)) return dir;
        }
        return null;
    }

    public ModuleResolution Resolve(string? reference, LuaSourceFile from)
    {
        if (reference is null)
            return new ModuleResolution.Unresolved("dynamic reference: argument is not a literal string");

        // SDK namespace. Every Lightroom SDK module is Lr-prefixed, which is what
        // makes this classification cheap and reliable.
        if (reference.StartsWith("Lr", StringComparison.Ordinal))
            return new ModuleResolution.External(reference);

        // A sibling file in the same plugin, addressed by stem.
        var dir = Path.GetDirectoryName(from.FullPath);
        if (dir is not null)
        {
            var candidate = Path.Combine(dir, reference + ".lua");
            if (File.Exists(candidate)) return new ModuleResolution.InGraph(Path.GetFullPath(candidate));
        }

        // Elsewhere in the SAME plugin. Deliberately not the whole tree: plugins
        // are independent deployment units, so a same-named file in a different
        // plugin is a coincidence, and edging to it would invent a dependency
        // that cannot exist at runtime.
        var pluginRoot = PluginRootOf(from.FullPath);
        if (pluginRoot is not null)
        {
            var match = Directory.EnumerateFiles(pluginRoot, reference + ".lua", SearchOption.AllDirectories).FirstOrDefault();
            if (match is not null) return new ModuleResolution.InGraph(Path.GetFullPath(match));
        }

        return new ModuleResolution.External(reference);
    }
}
