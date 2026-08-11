namespace RazorGraph.Lua.ExternalApis;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>One module in a catalogued external API surface.</summary>
/// <param name="FirstCataloguedIn">
/// Earliest catalogued version carrying it — NOT necessarily the release that
/// introduced it, since the catalogue starts wherever the oldest available SDK does.
/// </param>
/// <param name="AbsentAfter">
/// Last catalogued version carrying it, when it is gone from the newest. A removal
/// or rename, and a real compatibility signal.
/// </param>
/// <summary>One documented function on a module.</summary>
/// <param name="Since">
/// The release the vendor states introduced it. Authoritative, unlike anything
/// inferred from which packages happen to be on disk.
/// </param>
/// <param name="Params">Declared parameters as "name:type", type as the docs give it.</param>
public sealed record ApiFunction(string? Since = null, List<string>? Params = null);

/// <summary>One module in a catalogued external API surface.</summary>
/// <param name="FirstCataloguedIn">
/// Earliest CATALOGUED version carrying it. Says which packages are held, not
/// when the vendor shipped it -- LrDevelopController reads 8.0 here because that
/// is the oldest local SDK containing it, where Adobe documents 6.0.
/// </param>
/// <param name="AbsentAfter">
/// Last catalogued version carrying it, when it is gone from the newest. A removal
/// or rename, and a real compatibility signal.
/// </param>
/// <param name="FirstSupportedIn">
/// The release the vendor states introduced the module, taken as the earliest
/// across its functions. Preferred over FirstCataloguedIn wherever present,
/// because it is a fact about the product rather than about our collection.
/// </param>
/// <param name="Functions">Documented functions, with their own introduction versions.</param>
public sealed record ExternalApiModule(
    string FirstCataloguedIn,
    string? AbsentAfter = null,
    string? FirstSupportedIn = null,
    Dictionary<string, ApiFunction>? Functions = null)
{
    /// <summary>The version to reason about: the vendor's, falling back to ours.</summary>
    public string EffectiveSince => FirstSupportedIn ?? FirstCataloguedIn;
}

/// <summary>
/// A document shipped alongside the API surface, recorded by identity rather than
/// content: a hash is re-verifiable evidence that this exact file was the source,
/// which is what provenance needs. Parsing the prose would add a dependency in
/// order to learn something weaker.
/// </summary>
public sealed record ExternalApiDocument(string File, long Bytes, string Sha256);

/// <summary>
/// What one catalogued version's source package attested about itself.
/// </summary>
/// <param name="Declares">The version line the package states in its own readme.</param>
/// <param name="Build">
/// Exact build the package was cut from, where it stamps one. Pins the data far
/// more precisely than a version number; older packages carry no such stamp.
/// </param>
/// <param name="Guides">
/// Manuals shipped in the same package, by hash. Corroborates the readme from a
/// second artefact, and lets anyone confirm they hold the same files. Absent
/// where a package shipped none — Lightroom SDK 3.0's Manual folder is empty.
/// </param>
public sealed record ExternalApiProvenance(
    string Declares,
    string? Build = null,
    List<ExternalApiDocument>? Guides = null);

/// <summary>
/// A namespace the vendor ships and uses without documenting.
/// </summary>
/// <param name="SeenIn">Catalogued versions whose own sample code imports it.</param>
/// <param name="Evidence">Where it was observed, so the claim can be re-checked.</param>
public sealed record UndocumentedApiModule(List<string> SeenIn, List<string> Evidence);

/// <summary>
/// A key a plug-in manifest may declare.
/// </summary>
/// <param name="Source">
/// "samples" (observed in the vendor's examples -- exact, but only as complete as
/// their usage), "guide" (read from the specification), or "samples+guide".
/// </param>
/// <param name="Required">Null unless the specification said; the samples cannot tell.</param>
/// <param name="Type">Declared value type, where the specification gave one.</param>
/// <param name="UsedBySamples">How many example manifests declare it.</param>
public sealed record ManifestKey(string Source, bool? Required = null, string? Type = null, int UsedBySamples = 0);

/// <summary>What a catalogue concluded about one referenced module name.</summary>
public abstract record ExternalApiVerdict
{
    private ExternalApiVerdict() { }

    /// <summary>In the catalogue, with the versions that carry it.</summary>
    public sealed record Known(string Name, string FirstCataloguedIn, string? AbsentAfter) : ExternalApiVerdict;

    /// <summary>
    /// Real, but on no reference page. The vendor ships and uses it — their own
    /// sample code imports it — and documents nothing about it. Distinct from
    /// Known because anything built on it is built on observation, and distinct
    /// from unknown because it demonstrably exists.
    /// </summary>
    public sealed record KnownUndocumented(string Name, IReadOnlyList<string> Evidence) : ExternalApiVerdict;

    /// <summary>
    /// Not in any catalogued version. Deliberately NOT called "nonexistent": the
    /// catalogue stops at a version Adobe has long since moved past, so the most
    /// likely explanation for an unrecognised module is that it is newer than
    /// anything catalogued. Reporting it as invalid would be the absence-as-finding
    /// trap wearing a version number.
    /// </summary>
    public sealed record UnknownToCatalogue(string Name, string Reason) : ExternalApiVerdict;
}

/// <summary>
/// A versioned catalogue of an external API's module surface — the modules a host
/// application provides, which plug-in code references but does not contain.
///
/// This turns "someone else's module" into "someone else's module, and I know
/// which release it came from". That is what makes a minimum-host-version answer
/// possible, and what lets a typo be told apart from a newer API.
///
/// The shipped instance is Lightroom Classic. The shape is deliberately generic
/// and data-driven so other hosts — Neovim's vim.*, LOVE2D's love.*, OpenResty's
/// ngx.* — can be added as JSON without code, which is the extension path the
/// wider ecosystem would use.
/// </summary>
public sealed class ExternalApiCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("product")] public string Product { get; init; } = string.Empty;

    /// <summary>Which edition of the product this describes — "classic" for Lightroom Classic.</summary>
    [JsonPropertyName("variant")] public string Variant { get; init; } = string.Empty;

    [JsonPropertyName("catalogedVersions")] public List<string> CatalogedVersions { get; init; } = [];
    [JsonPropertyName("newestCataloguedVersion")] public string NewestCataloguedVersion { get; init; } = string.Empty;

    /// <summary>
    /// The newest release known to exist, catalogued or not. Carried so the
    /// catalogue can state how far behind it is rather than implying completeness.
    /// </summary>
    [JsonPropertyName("newestKnownRelease")] public string NewestKnownRelease { get; init; } = string.Empty;

    /// <summary>
    /// Per version, what the source package said about itself — the version line
    /// from its own Readme, and the build stamp where it carries one.
    ///
    /// Recorded so trust is visible rather than assumed. A catalogue claiming to
    /// be authoritative about a vendor's API surface is worth nothing if a reader
    /// cannot tell whether its contents came from that vendor.
    /// </summary>
    [JsonPropertyName("provenance")] public Dictionary<string, ExternalApiProvenance> Provenance { get; init; } = new(StringComparer.Ordinal);

    [JsonPropertyName("modulePrefix")] public string ModulePrefix { get; init; } = string.Empty;
    [JsonPropertyName("modules")] public Dictionary<string, ExternalApiModule> Modules { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Namespaces observed in the vendor's own sample code that appear on no
    /// reference page. Real, usable, and undescribed.
    /// </summary>
    [JsonPropertyName("undocumentedModules")] public Dictionary<string, UndocumentedApiModule> UndocumentedModules { get; init; } = new(StringComparer.Ordinal);

    /// <summary>
    /// Keys an Info.lua-style manifest may declare, with the type and
    /// required/optional flag where the specification supplied them.
    /// </summary>
    [JsonPropertyName("manifestKeys")] public Dictionary<string, ManifestKey> ManifestKeys { get; init; } = new(StringComparer.Ordinal);

    /// <summary>True when this catalogue stops short of the newest known release.</summary>
    public bool IsBehindLatest =>
        !string.IsNullOrEmpty(NewestKnownRelease)
        && !string.Equals(NewestCataloguedVersion, NewestKnownRelease, StringComparison.Ordinal);

    public ExternalApiVerdict Classify(string moduleName)
    {
        if (Modules.TryGetValue(moduleName, out var module))
            return new ExternalApiVerdict.Known(moduleName, module.FirstCataloguedIn, module.AbsentAfter);

        // Checked before the unknown case: these exist, and calling them unknown
        // would send a caller hunting for a typo that is not there.
        if (UndocumentedModules.TryGetValue(moduleName, out var undocumented))
            return new ExternalApiVerdict.KnownUndocumented(moduleName, undocumented.Evidence);

        var behind = IsBehindLatest
            ? $" This catalogue stops at {NewestCataloguedVersion} and {Product} has shipped through {NewestKnownRelease}, so it may simply be newer."
            : string.Empty;

        return new ExternalApiVerdict.UnknownToCatalogue(
            moduleName,
            $"not present in any catalogued version ({string.Join(", ", CatalogedVersions)}).{behind}");
    }

    /// <summary>
    /// The oldest catalogued version that carries every one of these modules — a
    /// plug-in's minimum host version, as far as the catalogue can see. Null when
    /// no single catalogued version carries them all, which usually means one of
    /// them is unknown rather than that no such release exists.
    /// </summary>
    public string? MinimumVersionFor(IEnumerable<string> moduleNames)
    {
        var needed = moduleNames
            .Select(n => Modules.TryGetValue(n, out var m) ? m : null)
            .Where(m => m is not null)
            .Select(m => m!.EffectiveSince)
            .ToList();

        return Highest(needed);
    }

    /// <summary>
    /// The minimum version for specific FUNCTIONS on a module, which is the
    /// question a caller actually has. A module existing is not the same as the
    /// function being called existing: LrDevelopController dates to 6.0 but
    /// carries functions introduced as late as 15.3, so importing it and calling
    /// one of those are very different requirements.
    /// </summary>
    /// <param name="functionNames">Unqualified names, as written after the dot.</param>
    public string? MinimumVersionForFunctions(string moduleName, IEnumerable<string> functionNames)
    {
        if (!Modules.TryGetValue(moduleName, out var module)) return null;

        var versions = functionNames
            .Select(f => module.Functions is not null && module.Functions.TryGetValue(f, out var fn) ? fn.Since : null)
            .Where(v => v is not null)
            .Select(v => v!)
            .ToList();

        // Nothing recognised: fall back to the module's own floor rather than
        // returning null, which would read as "no requirement".
        return versions.Count > 0 ? Highest(versions) : module.EffectiveSince;
    }

    /// <summary>
    /// Whether the catalogue carries this function on this module.
    ///
    /// Distinguishes "we know it and it is old" from "we have never heard of
    /// it", which <see cref="MinimumVersionForFunctions"/> deliberately blurs by
    /// falling back to the module's own floor. A caller reporting unknown
    /// functions needs the difference.
    /// </summary>
    public bool HasFunction(string moduleName, string functionName) =>
        Modules.TryGetValue(moduleName, out var module)
        && module.Functions is not null
        && module.Functions.ContainsKey(functionName);

    /// <summary>
    /// The latest of a set of version strings, compared as versions. Ordinal
    /// position in CatalogedVersions cannot be used: vendor-stated versions like
    /// 1.3 and 6.0 are mostly releases this catalogue does not hold.
    /// </summary>
    private static string? Highest(IReadOnlyCollection<string> versions)
    {
        if (versions.Count == 0) return null;

        string? best = null;
        Version? bestParsed = null;
        foreach (var v in versions)
        {
            if (!Version.TryParse(v, out var parsed)) continue;
            if (bestParsed is null || parsed > bestParsed) { bestParsed = parsed; best = v; }
        }
        return best;
    }

    /// <summary>Load a catalogue shipped inside this assembly.</summary>
    public static ExternalApiCatalog LoadEmbedded(string name)
    {
        var resource = $"RazorGraph.Lua.ExternalApis.{name}.json";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Embedded catalogue not found: {resource}");

        return JsonSerializer.Deserialize<ExternalApiCatalog>(stream, Options)
            ?? throw new InvalidOperationException($"Catalogue {name} failed to deserialize.");
    }
}
