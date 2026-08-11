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

    /// <summary>
    /// The SDK checks only this host can make. Language-level rules run for
    /// every host and are not listed here.
    /// </summary>
    public IEnumerable<Checks.ILuaRule> Rules => [new LightroomSdkRule(), new LightroomManifestRule()];

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
    /// Adobe's own sample plugins, which ship inside every SDK download under
    /// "Lightroom SDK &lt;version&gt;/Sample Plugins". Reference code the author did
    /// not write and will not change, so it is vendor by the same rule as a
    /// bundled JavaScript library.
    ///
    /// It matters here more than usual: unzipping three SDKs beside a plug-in
    /// project buries nine authored files under 115 Adobe ones, and every finding
    /// then needs manual attribution before it can be acted on.
    /// </summary>
    public string? VendorReason(LuaSourceFile file)
    {
        var segments = file.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var sdkIndex = Array.FindIndex(segments, s => s.StartsWith("Lightroom SDK ", StringComparison.OrdinalIgnoreCase));
        if (sdkIndex < 0) return null;

        // Only the samples are vendor SOURCE. Anything else an SDK folder holds
        // (the API reference, the manual) is not Lua and never reaches here.
        var isSample = segments.Skip(sdkIndex + 1)
            .Any(s => s.Equals("Sample Plugins", StringComparison.OrdinalIgnoreCase));

        return isSample ? $"Adobe sample plugin shipped in {segments[sdkIndex]}" : null;
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
        {
            module.SetProperty("minimumSdkVersion", minimum);

            // Say WHY, even when the reason is only an import. A bare "6.0" is
            // what the earlier misreading was built on: the number was right and
            // its meaning was assumed, and nothing on the node could settle it.
            // Marked "imported" so a raised floor from a real call is visibly a
            // different claim.
            var atFloor = known
                .Where(k => Catalog.Modules.TryGetValue(k.Name, out var m)
                            && string.Equals(m.EffectiveSince, minimum, StringComparison.Ordinal))
                .Select(k => $"{k.Name} ({minimum}, imported)")
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList();

            if (atFloor.Count > 0) module.SetProperty("minimumSdkVersionDrivenBy", atFloor);
        }

        // Real but undescribed: Adobe ships and imports these, and documents
        // nothing about them. Worth flagging separately -- code built on them is
        // built on observation -- but calling them unknown would send someone
        // hunting for a typo that is not there.
        var undocumented = verdicts.OfType<ExternalApiVerdict.KnownUndocumented>().Select(u => u.Name).ToList();
        if (undocumented.Count > 0) module.SetProperty("sdkModulesUndocumented", undocumented);

        var unknown = verdicts.OfType<ExternalApiVerdict.UnknownToCatalogue>().Select(u => u.Name).ToList();
        if (unknown.Count > 0)
        {
            module.SetProperty("sdkModulesUnknownToCatalogue", unknown);
            module.SetProperty("sdkCatalogueRange",
                $"catalogued {string.Join(", ", Catalog.CatalogedVersions)}; {Catalog.Product} has shipped through {Catalog.NewestKnownRelease}");
        }
    }

    /// <summary>
    /// What the code actually CALLS of the SDK, and the minimum version that
    /// implies.
    ///
    /// This is the honest form of a question the import list can only approximate.
    /// LrDevelopController has shipped since SDK 6.0 and carries functions added
    /// as late as 15.3, so importing it says 6.0 and calling setValue says 15.3 —
    /// and a plug-in that imports it without calling those functions runs on 6.0.
    /// Reported against Lori's own plug-ins as needing 8.0 before calls were
    /// extracted; the imports were real, the requirement was not.
    ///
    /// The floor is the HIGHEST of what the imports and the calls demand, because
    /// both are real: a module must exist to be imported at all, and a function
    /// must exist to be called. Whatever sets the floor is named, so the answer
    /// can be acted on rather than just believed.
    /// </summary>
    public void AnnotateExternalCalls(GraphNode node, IReadOnlyList<ExternalCall> calls)
    {
        var sdkCalls = calls.Where(c => IsSdkModule(c.Module)).ToList();
        if (sdkCalls.Count == 0) return;

        node.SetProperty("sdkCalls", sdkCalls
            .Select(c => $"{c.Module}.{c.Function}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList());

        // Already on the node from the import pass, and still valid: the module
        // has to be there before anything can be called on it.
        var required = node.GetProperty<string>("minimumSdkVersion");
        var importDrivers = node.GetProperty<List<string>>("minimumSdkVersionDrivenBy") ?? [];
        var callDrivers = new List<string>();
        var raised = false;

        foreach (var module in sdkCalls
            .GroupBy(c => c.Module, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            var functionNames = module.Select(c => c.Function).Distinct(StringComparer.Ordinal).ToList();
            var version = Catalog.MinimumVersionForFunctions(module.Key, functionNames);
            if (version is null) continue;

            var comparison = CompareVersions(version, required);
            if (comparison > 0)
            {
                required = version;
                callDrivers.Clear();
                raised = true;
            }
            if (comparison >= 0) callDrivers.Add($"{module.Key} ({version}, called)");
        }

        if (required is not null)
        {
            node.SetProperty("minimumSdkVersion", required);

            // A call that RAISES the floor replaces the explanation; one that
            // merely ties it joins the import that already implied it. An import
            // nothing calls keeps its own reason and stays visibly an import --
            // which is the whole distinction this pass exists to draw.
            List<string> drivers;
            if (raised)
            {
                drivers = callDrivers;
            }
            else
            {
                // One line per module, and "called" wins: a module that is both
                // imported and called is one fact, and the call is the stronger
                // half of it. Listing both doubled the explanation for every
                // module a file actually uses.
                var byModule = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var driver in importDrivers) byModule[ModuleOfDriver(driver)] = driver;
                foreach (var driver in callDrivers) byModule[ModuleOfDriver(driver)] = driver;

                drivers = byModule.Values.OrderBy(name => name, StringComparer.Ordinal).ToList();
            }

            if (drivers.Count > 0) node.SetProperty("minimumSdkVersionDrivenBy", drivers);
        }

        // Called but not catalogued. Not an error: the catalogue stops short of
        // the newest SDK, and 30 of 844 documented functions carry no version at
        // all, so the likeliest cause is our coverage rather than their code.
        var uncatalogued = sdkCalls
            .Where(c => !Catalog.HasFunction(c.Module, c.Function))
            .Select(c => $"{c.Module}.{c.Function}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        if (uncatalogued.Count > 0) node.SetProperty("sdkCallsNotInCatalogue", uncatalogued);
    }

    /// <summary>The module name out of a driver line: "LrView (1.3, called)" -> "LrView".</summary>
    private static string ModuleOfDriver(string driver)
    {
        var cut = driver.IndexOf(" (", StringComparison.Ordinal);
        return cut > 0 ? driver[..cut] : driver;
    }

    /// <summary>
    /// Compare two version strings, treating null as "nothing required yet".
    /// Positive when <paramref name="candidate"/> demands more.
    /// </summary>
    private static int CompareVersions(string candidate, string? current)
    {
        if (current is null) return 1;
        if (!Version.TryParse(candidate, out var left)) return 0;
        if (!Version.TryParse(current, out var right)) return 1;
        return left.CompareTo(right);
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
