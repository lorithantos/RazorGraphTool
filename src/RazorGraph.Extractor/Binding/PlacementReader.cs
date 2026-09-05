namespace RazorGraph.Extractor.Binding;

using System.Text.Json;

/// <summary>
/// One entry in an OrchardCore <c>placement.json</c>: a rule saying where a shape
/// renders, what extra names it may be served by, and — with <c>"place": "-"</c> —
/// whether it renders at all.
///
/// Config rather than code, and therefore invisible to every symbol-based pass,
/// while deciding the same binding question the drivers do.
/// </summary>
/// <param name="ShapeType">The key: the shape this rule applies to.</param>
/// <param name="Place">
/// The zone and order, or <c>-</c> for "do not render". Null when the entry omits
/// it, which is legal and leaves placement to some other provider.
/// </param>
/// <param name="Alternates">
/// Extra names tried BEFORE the shape's own, and optional by design: a missing
/// alternate template falls back to the base shape rather than failing.
/// </param>
/// <param name="Wrappers">
/// Shapes rendered around this one. Unlike alternates these are required — a
/// wrapper that resolves to nothing is rendered as a shape and throws.
/// </param>
/// <param name="RenamedTo">
/// The <c>shape</c> key, which substitutes a different shape type. The substitute
/// is what actually renders, so it is the name that must resolve.
/// </param>
/// <param name="Filters">
/// Names of the extra conditions on the entry (<c>contentType</c>, <c>path</c>,
/// and any other provider-supplied key). Only the names are kept: this pass needs
/// to know whether a rule is conditional, not to evaluate it.
/// </param>
internal sealed record PlacementEntry(
    string ShapeType,
    string? Place,
    string? DisplayType,
    string? Differentiator,
    IReadOnlyList<string> Alternates,
    IReadOnlyList<string> Wrappers,
    string? RenamedTo,
    IReadOnlyList<string> Filters,
    string FilePath,
    int Line)
{
    /// <summary>
    /// <c>"place": "-"</c> — the shape is dropped from the layout entirely. A shape
    /// that never renders cannot throw for want of a template, so this is the one
    /// thing that can retire an unbound-shape finding.
    /// </summary>
    public bool Hides => Place == "-";

    /// <summary>
    /// No display type, differentiator or filter: the rule applies on every render
    /// rather than in one case. Only an unconditional hide proves a shape never
    /// renders; a conditional one still leaves the other paths live.
    /// </summary>
    public bool IsUnconditional =>
        string.IsNullOrEmpty(DisplayType)
        && string.IsNullOrEmpty(Differentiator)
        && Filters.Count == 0;
}

/// <summary>
/// Reads OrchardCore <c>placement.json</c> files.
///
/// The schema is OrchardCore's <c>PlacementFile</c> — a map of shape type to an
/// array of rules — and this reader takes it from that type rather than from the
/// documentation, so the two cannot drift:
/// <c>place</c>, <c>displayType</c>, <c>differentiator</c>, <c>alternates</c>,
/// <c>wrappers</c>, <c>shape</c>, plus arbitrary filter keys collected by
/// <c>[JsonExtensionData]</c>.
///
/// These files are JSON with comments. OrchardCore.Contents ships one with a
/// <c>/* */</c> banner listing the available shapes AND a block of <c>//</c>-commented
/// example entries, so a strict parser fails on the framework's own file. The
/// runtime reads them with comments skipped and trailing commas allowed; this
/// reader matches, because parsing the corpus is the whole job.
/// </summary>
internal static class PlacementReader
{
    /// <summary>The file name OrchardCore looks for in an extension's root.</summary>
    public const string FileName = "placement.json";

    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonReaderOptions ReaderOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>Entries in one placement file, in document order.</summary>
    public static IReadOnlyList<PlacementEntry> Read(string path) =>
        Parse(File.ReadAllText(path), path);

    /// <inheritdoc cref="Read"/>
    /// <remarks>
    /// Public so the grammar can be exercised on text: the parsing rules are the
    /// part that has to survive a corpus, and requiring a file on disk to test
    /// them would put the interesting cases out of reach.
    /// </remarks>
    public static IReadOnlyList<PlacementEntry> Parse(string text, string path)
    {
        var entries = new List<PlacementEntry>();

        using var document = JsonDocument.Parse(text, Options);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return entries;

        var lines = KeyLines(text);

        foreach (var shape in document.RootElement.EnumerateObject())
        {
            if (shape.Value.ValueKind != JsonValueKind.Array) continue;
            var line = lines.TryGetValue(shape.Name, out var found) ? found : 0;

            foreach (var rule in shape.Value.EnumerateArray())
            {
                if (rule.ValueKind != JsonValueKind.Object) continue;
                entries.Add(ReadRule(shape.Name, rule, path, line));
            }
        }

        return entries;
    }

    private static PlacementEntry ReadRule(string shapeType, JsonElement rule, string path, int line)
    {
        string? place = null, displayType = null, differentiator = null, renamedTo = null;
        var alternates = new List<string>();
        var wrappers = new List<string>();
        var filters = new List<string>();

        foreach (var property in rule.EnumerateObject())
        {
            switch (property.Name)
            {
                case "place": place = AsString(property.Value); break;
                case "displayType": displayType = AsString(property.Value); break;
                case "differentiator": differentiator = AsString(property.Value); break;
                case "shape": renamedTo = AsString(property.Value); break;
                case "alternates": AddStrings(alternates, property.Value); break;
                case "wrappers": AddStrings(wrappers, property.Value); break;

                // Everything else is a match condition — contentType, contentPart,
                // path, or one a third-party IPlacementNodeFilterProvider added.
                // Unknown keys are conditions too, so they are collected rather
                // than ignored: the count is what decides whether a hide is
                // unconditional.
                default: filters.Add(property.Name); break;
            }
        }

        return new PlacementEntry(
            shapeType, place, displayType, differentiator, alternates, wrappers, renamedTo, filters, path, line);
    }

    private static string? AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>
    /// A string array, tolerating the single-string spelling. OrchardCore's filter
    /// keys accept either form (<c>"contentType": "Menu"</c> and
    /// <c>"contentType": [ "Menu" ]</c> both appear in the corpus), so the list
    /// keys are read the same way rather than assuming the array.
    /// </summary>
    private static void AddStrings(List<string> into, JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() is { Length: > 0 } single) into.Add(single);
            return;
        }

        if (value.ValueKind != JsonValueKind.Array) return;

        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } name) into.Add(name);
        }
    }

    /// <summary>
    /// Line number of each top-level key.
    ///
    /// JsonDocument does not carry positions, and finding the key by text search
    /// would land in the wrong place on exactly the file that motivated comment
    /// support: OrchardCore.Contents lists <c>Parts_Contents_Publish</c> in its
    /// banner comment eight lines above the real entry. Re-reading with
    /// Utf8JsonReader — which skips comments and reports token offsets — puts the
    /// finding on the line a reader would edit.
    /// </summary>
    private static Dictionary<string, int> KeyLines(string text)
    {
        var lines = new Dictionary<string, int>(StringComparer.Ordinal);
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        var reader = new Utf8JsonReader(bytes, ReaderOptions);

        while (reader.Read())
        {
            if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) continue;

            var name = reader.GetString();
            // First writer wins: a duplicated key is the file contradicting
            // itself, and the first is the one a reader sees.
            if (name is not null && !lines.ContainsKey(name)) lines[name] = LineAt(bytes, reader.TokenStartIndex);
        }

        return lines;
    }

    private static int LineAt(ReadOnlySpan<byte> bytes, long offset)
    {
        var line = 1;
        var end = (int)Math.Min(offset, bytes.Length);
        for (var i = 0; i < end; i++)
        {
            if (bytes[i] == (byte)'\n') line++;
        }
        return line;
    }
}
