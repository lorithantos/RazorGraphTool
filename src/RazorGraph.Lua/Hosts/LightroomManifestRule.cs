namespace RazorGraph.Lua.Hosts;

using System.Globalization;
using RazorGraph.Core.Graph;
using RazorGraph.Lua.Checks;

/// <summary>
/// Checks a plug-in's <c>Info.lua</c> against the catalogued manifest vocabulary
/// and against what its own code actually needs.
///
/// The manifest is the one file Lightroom reads before anything runs, so a
/// mistake here fails the whole plug-in rather than one code path — and it fails
/// at load, where there is no stack to read. Two of its keys are documented as
/// required; the rest of the vocabulary was recovered from Adobe's guide and
/// their sample plug-ins, and its coverage is stated rather than assumed.
///
/// The check worth having is the one no vocabulary can give: does the version
/// the manifest CLAIMS to support match what the code actually calls. A plug-in
/// declaring LrSdkMinimumVersion 3.0 while calling a function introduced in 15.3
/// installs happily on 3.0 and then fails when that path runs.
/// </summary>
internal sealed class LightroomManifestRule : ILuaRule
{
    private const string ManifestFile = "Info.lua";

    public string Id => "lightroom.manifest";

    public string Title => "Info.lua keys, and whether the declared SDK version covers the code";

    public IEnumerable<LuaFinding> Check(LuaCheckContext context)
    {
        var catalog = LightroomHost.Catalog;
        var vocabulary = catalog.ManifestKeys;

        foreach (var manifest in context.Declarations.Where(d =>
            string.Equals(Path.GetFileName(d.File.RelativePath), ManifestFile, StringComparison.OrdinalIgnoreCase)))
        {
            var file = manifest.File.RelativePath;

            if (manifest.ReturnedFields is null)
            {
                // Legal Lua -- the table may be built up and returned by name --
                // and simply not checkable here. Said out loud, because silence
                // would read as a clean manifest.
                yield return new LuaFinding(
                    Id, LuaSeverity.Note, file, 0,
                    "manifest fields could not be read: the file does not return a table literal",
                    "keys are only checkable when Info.lua ends in return { ... }");
                continue;
            }

            var declared = manifest.ReturnedFields.ToDictionary(f => f.Key, StringComparer.Ordinal);

            foreach (var required in vocabulary
                .Where(k => k.Value.Required == true)
                .Select(k => k.Key)
                .OrderBy(key => key, StringComparer.Ordinal))
            {
                if (!declared.ContainsKey(required))
                {
                    yield return new LuaFinding(
                        Id, LuaSeverity.Error, file, 0,
                        $"manifest is missing the required key {required}",
                        $"documented as required in {catalog.Product} SDK {catalog.NewestCataloguedVersion}");
                }
            }

            // NO TYPE CHECK. The catalogue records a type per key, and comparing
            // against it fired on six of Adobe's own manifests -- flickr and
            // custommetadatasample both pass LrMetadataProvider as a string where
            // the guide documents a table, because Lightroom accepts either the
            // table or the name of a file returning one. A rule that flags the
            // reference implementation is wrong about the rule, not about the
            // reference, and one noisy check discredits the rest of the report.
            // The catalogue's type field is not wrong so much as too coarse to
            // decide anything, so nothing is decided from it.

            // One note per manifest rather than per key: this is a statement
            // about the catalogue's coverage, and repeating it per key turns one
            // known gap into a wall of findings. VERSION alone accounted for 30.
            var unknown = manifest.ReturnedFields
                .Where(f => !vocabulary.ContainsKey(f.Key))
                .Select(f => f.Key)
                .Distinct(StringComparer.Ordinal)
                .OrderBy(key => key, StringComparer.Ordinal)
                .ToList();

            if (unknown.Count > 0)
            {
                // Bounded by OUR vocabulary: 22 keys recovered from the guide and
                // the samples, against an SDK that has kept shipping. A key we do
                // not know is far likelier to be newer than wrong.
                yield return new LuaFinding(
                    Id, LuaSeverity.Note, file, 0,
                    $"manifest key(s) not in the catalogued vocabulary: {string.Join(", ", unknown)}",
                    $"catalogue holds {vocabulary.Count} keys from {catalog.Product} SDK {catalog.NewestCataloguedVersion}");
            }

            foreach (var finding in CheckDeclaredVersion(context, manifest, declared, file))
            {
                yield return finding;
            }
        }
    }

    /// <summary>
    /// Compare the version the manifest promises to run on against the floor the
    /// plug-in's own code implies.
    ///
    /// Scoped to the plug-in that owns this manifest, not the tree: a folder can
    /// hold several .lrdevplugin directories, and they ship independently.
    /// </summary>
    private IEnumerable<LuaFinding> CheckDeclaredVersion(
        LuaCheckContext context,
        LuaFileDeclarations manifest,
        Dictionary<string, LuaManifestField> declared,
        string file)
    {
        if (!declared.TryGetValue("LrSdkMinimumVersion", out var minimumField)) yield break;
        if (!TryParseVersion(minimumField.Value, out var declaredMinimum)) yield break;

        var pluginRoot = PluginFolderOf(manifest.File.RelativePath);

        string? highest = null;
        string? driver = null;

        foreach (var module in context.Graph.Nodes.Where(n => n.ForeignType == LuaGraphBuilder.ModuleKind))
        {
            var path = module.GetProperty<string>("relativePath");
            if (path is null || PluginFolderOf(path) != pluginRoot) continue;

            var floor = module.GetProperty<string>("minimumSdkVersion");
            if (!TryParseVersion(floor, out var parsed)) continue;

            if (highest is null || parsed > VersionOf(highest))
            {
                highest = floor;
                var drivers = module.GetProperty<List<string>>("minimumSdkVersionDrivenBy") ?? [];
                driver = drivers.Count > 0
                    ? $"{Path.GetFileName(path)} needs it: {string.Join(", ", drivers)}"
                    : $"{Path.GetFileName(path)} needs it";
            }
        }

        if (highest is null || VersionOf(highest) <= declaredMinimum) yield break;

        yield return new LuaFinding(
            Id, LuaSeverity.Error, file, minimumField.Line,
            $"manifest declares LrSdkMinimumVersion {minimumField.Value} but the plug-in's code needs {highest}",
            driver);
    }

    /// <summary>The .lrdevplugin folder a path sits under, or the whole path when there is none.</summary>
    private static string PluginFolderOf(string relativePath)
    {
        var segments = relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var plugin = segments.FirstOrDefault(s => s.EndsWith(".lrdevplugin", StringComparison.OrdinalIgnoreCase));
        return plugin ?? Path.GetDirectoryName(relativePath) ?? "";
    }

    private static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        if (string.IsNullOrWhiteSpace(text)) return false;

        // Manifests write 3.0 and 15.3; Version wants at least a major and minor,
        // and a bare "6" is legal Lua for a number.
        var normalised = text.Contains('.') ? text : $"{text}.0";
        return Version.TryParse(normalised, out version!);
    }

    private static Version VersionOf(string text) =>
        TryParseVersion(text, out var version) ? version : new Version(0, 0);
}
