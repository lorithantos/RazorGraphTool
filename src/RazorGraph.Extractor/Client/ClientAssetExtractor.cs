namespace RazorGraph.Extractor.Client;

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

    private static readonly string[] VendorMarkers =
    {
        $"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}node_modules{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"
    };

    /// <summary>
    /// Scan the project's wwwroot for first-party .js and .css files. Vendor
    /// bundles (wwwroot/lib, node_modules) and minified builds are skipped:
    /// they are not edited, so graphing them is cost without signal.
    /// </summary>
    public List<ClientAssetInfo> ExtractAssets(string projectDir, string? idScope = null)
    {
        var webRoot = Path.Combine(projectDir, "wwwroot");
        if (!Directory.Exists(webRoot)) return new List<ClientAssetInfo>();

        var assets = new List<ClientAssetInfo>();

        foreach (var file in Directory.EnumerateFiles(webRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(file);
            var isScript = ext.Equals(".js", StringComparison.OrdinalIgnoreCase);
            var isStyle = ext.Equals(".css", StringComparison.OrdinalIgnoreCase);
            if (!isScript && !isStyle) continue;
            if (IsVendorOrMinified(file)) continue;

            assets.Add(BuildAsset(file, projectDir, isScript, idScope));
        }

        return assets;
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

    private static bool IsVendorOrMinified(string file)
    {
        if (VendorMarkers.Any(m => file.Contains(m, StringComparison.OrdinalIgnoreCase))) return true;
        var name = Path.GetFileName(file);
        return name.Contains(".min.", StringComparison.OrdinalIgnoreCase);
    }

    private ClientAssetInfo BuildAsset(string file, string projectDir, bool isScript, string? idScope)
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
