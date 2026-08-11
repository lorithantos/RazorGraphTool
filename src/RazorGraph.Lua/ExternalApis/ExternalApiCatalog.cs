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
public sealed record ExternalApiModule(string FirstCataloguedIn, string? AbsentAfter);

/// <summary>
/// What one catalogued version's source package attested about itself.
/// </summary>
/// <param name="Declares">The version line the package states in its own readme.</param>
/// <param name="Build">
/// Exact build the package was cut from, where it stamps one. Pins the data far
/// more precisely than a version number; older packages carry no such stamp.
/// </param>
public sealed record ExternalApiProvenance(string Declares, string? Build = null);

/// <summary>What a catalogue concluded about one referenced module name.</summary>
public abstract record ExternalApiVerdict
{
    private ExternalApiVerdict() { }

    /// <summary>In the catalogue, with the versions that carry it.</summary>
    public sealed record Known(string Name, string FirstCataloguedIn, string? AbsentAfter) : ExternalApiVerdict;

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

    /// <summary>True when this catalogue stops short of the newest known release.</summary>
    public bool IsBehindLatest =>
        !string.IsNullOrEmpty(NewestKnownRelease)
        && !string.Equals(NewestCataloguedVersion, NewestKnownRelease, StringComparison.Ordinal);

    public ExternalApiVerdict Classify(string moduleName)
    {
        if (Modules.TryGetValue(moduleName, out var module))
            return new ExternalApiVerdict.Known(moduleName, module.FirstCataloguedIn, module.AbsentAfter);

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
            .Select(m => m!.FirstCataloguedIn)
            .ToList();

        if (needed.Count == 0) return null;

        // Ordinal position in the catalogue, so this does not need to parse
        // version strings that other products may not number the same way.
        var latest = needed.Max(v => CatalogedVersions.IndexOf(v));
        return latest >= 0 ? CatalogedVersions[latest] : null;
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
