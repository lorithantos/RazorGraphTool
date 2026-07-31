namespace RazorGraph.Extractor.Client;

using System.Text.Json;
using System.Text.RegularExpressions;

/// <summary>
/// Extracts client-side assets (JavaScript and CSS under wwwroot) and the
/// server-to-client coupling that Razor pages create with them.
///
/// Three couplings are recovered, all of them text-shaped because there is no
/// compiler model for JavaScript:
///   1. which assets a page pulls in (script src / link href),
///   2. which data-* keys a page renders from server state, and which keys the
///      JavaScript then reads back out of the DOM,
///   3. which API routes the JavaScript calls with fetch/ajax.
///
/// Heuristic by design. A missed edge costs a query result; a false edge costs
/// trust, so every pattern here requires an explicit literal rather than
/// inferring from a variable.
/// </summary>
public sealed class ClientAssetExtractor
{
    // dataset.fooBar -> the DOM maps this to the data-foo-bar attribute.
    private static readonly Regex DatasetAccessRegex = new(
        @"\.dataset\s*\.\s*(?<key>[A-Za-z_][\w$]*)", RegexOptions.Compiled);

    // getAttribute('data-x'), setAttribute("data-x", ...), removeAttribute, hasAttribute.
    private static readonly Regex AttributeCallRegex = new(
        @"(?:get|set|has|remove)Attribute\s*\(\s*(?:""|')data-(?<key>[\w-]+)(?:""|')", RegexOptions.Compiled);

    // Assignment into the DOM: el.dataset.x = ... . The negative lookahead keeps
    // comparisons (== / ===) on the read side where they belong.
    private static readonly Regex DatasetWriteRegex = new(
        @"\.dataset\s*\.\s*(?<key>[A-Za-z_][\w$]*)\s*=(?!=)", RegexOptions.Compiled);

    private static readonly Regex AttributeWriteRegex = new(
        @"(?:set|remove)Attribute\s*\(\s*(?:""|')data-(?<key>[\w-]+)(?:""|')", RegexOptions.Compiled);

    // Attribute selectors: querySelector('[data-x]'), '.btn[data-x="1"]'.
    private static readonly Regex AttributeSelectorRegex = new(
        @"\[\s*data-(?<key>[\w-]+)", RegexOptions.Compiled);

    // fetch('/api/...'), fetch(`/api/...`). Only literal-leading URLs; a template
    // expression yields the literal prefix, which is enough to bind a controller.
    private static readonly Regex FetchUrlRegex = new(
        @"fetch\s*\(\s*(?<q>[""'`])(?<url>[^""'`$]+)", RegexOptions.Compiled);

    // $.ajax({ url: '/api/...' }) and $.post/$.get('/api/...').
    private static readonly Regex JQueryUrlRegex = new(
        @"(?:url\s*:\s*|\$\.(?:get|post|getJSON)\s*\(\s*)(?:""|')(?<url>/[^""']+)", RegexOptions.Compiled);

    // Directory names that mark everything beneath them as third-party. Matched
    // as whole path segments, never substrings: the original substring rule
    // ("\lib\") silently admitted nopCommerce's lib_npm — 102k LOC of moment
    // locales and elfinder classified as first-party by one unmatched name.
    private static readonly HashSet<string> VendorDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "lib", "lib_npm", "node_modules", "bower_components", "vendor"
    };

    // Build output is not source under either policy; includeVendor does not
    // resurrect it and it is not counted as a skipped asset.
    private static readonly HashSet<string> BuildOutputDirNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj"
    };

    /// <summary>One asset the vendor policy dropped, and why.</summary>
    public sealed record SkippedAsset(string RelativePath, string Reason);

    /// <summary>Assets the most recent <see cref="ExtractAssets"/> call dropped as vendor.</summary>
    public IReadOnlyList<SkippedAsset> LastSkipped => _lastSkipped;
    private readonly List<SkippedAsset> _lastSkipped = new();

    /// <summary>
    /// Scan the project's wwwroot for .js and .css files.
    ///
    /// Detection and policy are separate. Every file is *classified* (vendor
    /// directory name, npm scope, shipped package manifest, package-drop
    /// evidence, minified); whether vendor files are then dropped is the
    /// <paramref name="includeVendor"/> switch. The default drops them — vendor
    /// code is not edited, so graphing it is cost without signal — but hunting a
    /// bug inside a shipped bundle is a legitimate reason to keep them, and that
    /// must be a flag rather than a code edit. Included vendor assets carry
    /// <see cref="ClientAssetInfo.IsVendor"/> and the reason, so consumers can
    /// still tell the tiers apart. Whatever is dropped is recorded in
    /// <see cref="LastSkipped"/>: a silent skip reads as "covered everything".
    /// </summary>
    public List<ClientAssetInfo> ExtractAssets(string projectDir, string? idScope = null, bool includeVendor = false)
    {
        _lastSkipped.Clear();
        var webRoot = Path.Combine(projectDir, "wwwroot");
        if (!Directory.Exists(webRoot)) return new List<ClientAssetInfo>();

        var context = new VendorContext(
            FindPackageDropRoots(projectDir, webRoot),
            FindShippedManifestDirs(webRoot));
        var assets = new List<ClientAssetInfo>();

        foreach (var file in Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            var isScript = ext.Equals(".js", StringComparison.OrdinalIgnoreCase);
            var isStyle = ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
            if (!isScript && !isStyle) continue;

            var segments = Path.GetRelativePath(webRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (segments.Any(s => BuildOutputDirNames.Contains(s))) continue;

            var vendorReason = ClassifyVendor(segments, context);
            if (vendorReason != null && !includeVendor)
            {
                _lastSkipped.Add(new SkippedAsset(
                    NormalizeRelative(Path.GetRelativePath(projectDir, file)), vendorReason));
                continue;
            }

            assets.Add(BuildAsset(file, projectDir, isScript, idScope, vendorReason));
        }

        return assets;
    }

    private sealed record VendorContext(HashSet<string> DropRoots, List<string[]> ManifestDirs);

    /// <summary>
    /// Why this wwwroot-relative path is vendor, or null when it is first-party.
    /// Checks are ordered cheapest-first; the first reason wins.
    /// </summary>
    private static string? ClassifyVendor(string[] segments, VendorContext context)
    {
        var dirs = segments[..^1];

        foreach (var dir in dirs)
        {
            if (VendorDirNames.Contains(dir)) return $"vendor directory '{dir}'";
            // Only an npm copy produces a directory literally named "@scope".
            if (dir.Length > 1 && dir[0] == '@') return $"npm scope '{dir}'";
        }

        if (dirs.Length > 0 && context.DropRoots.Contains(dirs[0]))
            return $"package drop '{dirs[0]}'";

        foreach (var manifestDir in context.ManifestDirs)
        {
            if (manifestDir.Length <= dirs.Length &&
                manifestDir.Zip(dirs).All(p =>
                    string.Equals(p.First, p.Second, StringComparison.OrdinalIgnoreCase)))
            {
                return $"package manifest in '{string.Join('/', manifestDir)}'";
            }
        }

        if (segments[^1].Contains(".min.", StringComparison.OrdinalIgnoreCase)) return "minified";

        return null;
    }

    /// <summary>
    /// Immediate children of wwwroot that are npm package drops: directories
    /// whose own child names overlap the dependency names in the project's root
    /// package.json. nopCommerce's wwwroot\lib_npm is the motivating case — a
    /// gulp task copies node_modules content there and leaves no manifest behind
    /// to prove it. Two matches are required: one shared name is coincidence,
    /// two is a copy task.
    /// </summary>
    private static HashSet<string> FindPackageDropRoots(string projectDir, string webRoot)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var dependencyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifestPath in EnumerateProjectManifests(projectDir))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath));
                foreach (var section in new[] { "dependencies", "devDependencies" })
                {
                    if (!doc.RootElement.TryGetProperty(section, out var deps) ||
                        deps.ValueKind != JsonValueKind.Object) continue;

                    foreach (var dep in deps.EnumerateObject())
                    {
                        // "@scope/name" installs under a directory literally called "@scope".
                        dependencyNames.Add(dep.Name.Split('/')[0]);
                    }
                }
            }
            catch (JsonException)
            {
                // a malformed package.json is not evidence of anything
            }
        }
        if (dependencyNames.Count == 0) return roots;

        foreach (var dir in Directory.EnumerateDirectories(webRoot))
        {
            var matches = Directory.EnumerateDirectories(dir)
                .Count(child => dependencyNames.Contains(Path.GetFileName(child)));
            if (matches >= 2) roots.Add(Path.GetFileName(dir));
        }

        return roots;
    }

    /// <summary>
    /// The project's own package manifests: one at the project root (the
    /// nopCommerce shape) or one level down in a build-asset directory like
    /// Assets/ or ClientApp/ (the OrchardCore shape, where each module's
    /// Assets\package.json names the packages its build copies into wwwroot).
    /// One level only — deeper manifests belong to the packages themselves.
    /// </summary>
    private static IEnumerable<string> EnumerateProjectManifests(string projectDir)
    {
        var rootManifest = Path.Combine(projectDir, "package.json");
        if (File.Exists(rootManifest)) yield return rootManifest;

        foreach (var dir in Directory.EnumerateDirectories(projectDir))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("wwwroot", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                BuildOutputDirNames.Contains(name)) continue;

            var nested = Path.Combine(dir, "package.json");
            if (File.Exists(nested)) yield return nested;
        }
    }

    /// <summary>
    /// wwwroot-relative directories that ship their own package manifest. Nobody
    /// hand-writes a package.json inside wwwroot; it came along with a copied
    /// package, and everything beneath it did too.
    /// </summary>
    private static List<string[]> FindShippedManifestDirs(string webRoot)
    {
        var dirs = new List<string[]>();
        foreach (var name in new[] { "package.json", "bower.json", ".bower.json" })
        {
            foreach (var file in Directory.EnumerateFiles(webRoot, name, SearchOption.AllDirectories))
            {
                var relativeDir = Path.GetRelativePath(webRoot, Path.GetDirectoryName(file)!);
                if (relativeDir == ".") continue; // wwwroot itself is not a package
                dirs.Add(relativeDir.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
        }
        return dirs;
    }

    /// <summary>
    /// Asset ids are project-relative, so a solution build scopes them by project
    /// name for the same reason page ids are scoped. Null scope leaves the
    /// single-project form untouched.
    /// </summary>
    public static string AssetId(string? idScope, bool isScript, string relativePath)
    {
        var prefix = isScript ? "js" : "css";
        return idScope is null ? $"{prefix}:{relativePath}" : $"{prefix}:{idScope}/{relativePath}";
    }

    /// <summary>
    /// An inline &lt;script&gt; block as an asset. It reuses every scanner the file
    /// path uses — the coupling a page creates with its own script block is the
    /// same coupling it creates with a file, and reporting them differently would
    /// mean the answer to "who reads this data key" depends on where the author
    /// happened to put the code.
    /// </summary>
    public static ClientAssetInfo BuildInlineScript(
        string? idScope,
        string pageRelativePath,
        string pageFilePath,
        string body,
        int line,
        int lineCount)
    {
        var relative = $"{NormalizeRelative(pageRelativePath)}#inline-{line}";

        var info = new ClientAssetInfo
        {
            Id = AssetId(idScope, isScript: true, relative),
            Name = $"{Path.GetFileName(pageRelativePath)}:{line}",
            FilePath = pageFilePath,
            RelativePath = relative,
            IsScript = true,
            IsInline = true,
            LineStart = line,
            LineCount = lineCount
        };

        PopulateFromScript(info, body);
        return info;
    }

    private ClientAssetInfo BuildAsset(string file, string projectDir, bool isScript, string? idScope, string? vendorReason)
    {
        var relative = NormalizeRelative(Path.GetRelativePath(projectDir, file));
        string text;
        try
        {
            text = File.ReadAllText(file);
        }
        catch (IOException)
        {
            // An unreadable asset still exists as a node; it just has no edges.
            text = string.Empty;
        }

        var info = new ClientAssetInfo
        {
            Id = AssetId(idScope, isScript, relative),
            Name = Path.GetFileName(file),
            FilePath = file,
            RelativePath = relative,
            IsScript = isScript,
            IsVendor = vendorReason != null,
            VendorReason = vendorReason,
            LineCount = text.Length == 0 ? 0 : text.Count(c => c == '\n') + 1
        };

        if (!isScript || text.Length == 0) return info;

        PopulateFromScript(info, text);
        return info;
    }

    /// <summary>
    /// Run every client-side scanner over one body of JavaScript. Shared by the
    /// file path and the inline path so the two cannot drift apart.
    /// </summary>
    private static void PopulateFromScript(ClientAssetInfo info, string text)
    {
        if (text.Length == 0) return;

        foreach (Match m in DatasetAccessRegex.Matches(text))
            info.DataKeys.Add(CamelToKebab(m.Groups["key"].Value));

        foreach (Match m in AttributeCallRegex.Matches(text))
            info.DataKeys.Add(m.Groups["key"].Value.ToLowerInvariant());

        foreach (Match m in AttributeSelectorRegex.Matches(text))
            info.DataKeys.Add(m.Groups["key"].Value.ToLowerInvariant());

        foreach (Match m in DatasetWriteRegex.Matches(text))
            info.DataKeysWritten.Add(CamelToKebab(m.Groups["key"].Value));

        foreach (Match m in AttributeWriteRegex.Matches(text))
            info.DataKeysWritten.Add(m.Groups["key"].Value.ToLowerInvariant());

        foreach (Match m in FetchUrlRegex.Matches(text).Concat(JQueryUrlRegex.Matches(text)))
        {
            var url = m.Groups["url"].Value.Trim();
            if (url.StartsWith('/')) info.ApiCalls.Add(url);
        }
    }

    /// <summary>
    /// Resolve an href/src as written in Razor ("~/js/site.js", "/js/site.js")
    /// to the wwwroot-relative form used in asset ids. Query strings and cache
    /// busters are dropped. Returns null for absolute/CDN URLs.
    /// </summary>
    public static string? ResolveAssetPath(string href)
    {
        if (string.IsNullOrWhiteSpace(href)) return null;
        if (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("//", StringComparison.Ordinal)) return null;

        var path = href.Split('?', '#')[0].TrimStart('~').TrimStart('/', '\\');
        if (path.Length == 0) return null;

        return NormalizeRelative(Path.Combine("wwwroot", path));
    }

    /// <summary>
    /// dataset.fileName -> file-name. The DOM's documented mapping: each
    /// uppercase letter becomes a dash plus its lowercase form.
    /// </summary>
    internal static string CamelToKebab(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length + 4);
        foreach (var c in key)
        {
            if (char.IsUpper(c))
            {
                sb.Append('-').Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }

    // Ids must be stable across machines and OSes, so they use forward slashes
    // regardless of what the filesystem hands back.
    private static string NormalizeRelative(string path) => path.Replace('\\', '/');
}

public sealed class ClientAssetInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public bool IsScript { get; init; }

    /// <summary>True when this is a &lt;script&gt; block inside a Razor file rather than a wwwroot file.</summary>
    public bool IsInline { get; init; }

    /// <summary>
    /// True when vendor detection classified this as third-party or minified.
    /// Vendor assets only appear at all when extraction was asked to include them.
    /// </summary>
    public bool IsVendor { get; init; }

    /// <summary>Which rule classified it: vendor directory, npm scope, shipped manifest, package drop, or minified.</summary>
    public string? VendorReason { get; init; }

    /// <summary>For inline blocks, the line in the page where the block opens.</summary>
    public int? LineStart { get; init; }

    public int LineCount { get; init; }

    /// <summary>data-* attribute keys this script touches at all, kebab-cased.</summary>
    public HashSet<string> DataKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Keys this script assigns into the DOM. A key it writes is state it owns,
    /// so its absence from the server-rendered markup is not a defect.
    /// </summary>
    public HashSet<string> DataKeysWritten { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Keys the script only ever reads — the ones the server must supply.</summary>
    public IEnumerable<string> DataKeysReadOnly => DataKeys.Except(DataKeysWritten, StringComparer.OrdinalIgnoreCase);

    /// <summary>Literal app-relative URLs this script calls.</summary>
    public HashSet<string> ApiCalls { get; } = new(StringComparer.OrdinalIgnoreCase);
}
