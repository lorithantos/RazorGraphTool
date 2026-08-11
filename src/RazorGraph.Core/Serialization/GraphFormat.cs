namespace RazorGraph.Core.Serialization;

using System.Globalization;

/// <summary>
/// A saved-graph format version, as written into <c>formatVersion</c>.
/// Major changes break readers; minor changes are additive.
/// </summary>
public readonly record struct GraphFormatVersion(int Major, int Minor)
{
    public override string ToString() => $"{Major}.{Minor}";
}

/// <summary>
/// What a reader concluded about the format version it found. <see cref="Version"/>
/// is null when the document carried no stamp at all.
/// </summary>
/// <param name="Supported">
/// False means refuse the document rather than read it: the caveat says why.
/// </param>
/// <param name="Caveat">
/// Non-null when the load is not a plain same-version read. It is written to be
/// repeated verbatim to the user — an unreported version mismatch is the exact
/// silent drift this stamp exists to prevent.
/// </param>
public sealed record GraphFormatAssessment(GraphFormatVersion? Version, bool Supported, string? Caveat)
{
    /// <summary>How to name this version in a response envelope.</summary>
    public string Display => Version?.ToString() ?? "unstamped";
}

/// <summary>
/// The saved-graph JSON format contract.
///
/// Every graph this build writes carries <c>formatVersion</c>. The stamp exists
/// for readers that did not write the file: a third-party extractor emitting
/// this schema, or an older server loading a newer graph. Without it, a format
/// change is undetectable — the document still parses, and the wrong reading is
/// silent. That is the failure mode DESIGN-NOTES section 1 exists to prevent, so
/// an unreadable major version is a hard refusal, not a best-effort parse.
///
/// Version policy:
///   MAJOR — the meaning of existing fields changed, or a field a reader must
///           understand was added. Readers refuse a newer major.
///   MINOR — additive only: new node/edge kinds, new properties. Older readers
///           can still read it, and the tolerant enum handling is what keeps
///           unknown vocabulary from throwing. Reported, never refused.
/// </summary>
public static class GraphFormat
{
    /// <summary>The version this build writes.</summary>
    public static readonly GraphFormatVersion Current = new(1, 0);

    /// <summary>The JSON property name carrying the stamp.</summary>
    public const string PropertyName = "formatVersion";

    /// <summary>
    /// Parse a stamp. A stamp that cannot be parsed is an error rather than a
    /// shrug: writer and reader disagreeing about the format OF the format is
    /// worse evidence of drift than no stamp at all.
    /// </summary>
    public static GraphFormatVersion Parse(string text)
    {
        var parts = (text ?? string.Empty).Split('.');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minor))
        {
            throw new InvalidOperationException(
                $"Unreadable {PropertyName} '{text}'. Expected MAJOR.MINOR, e.g. '{Current}'.");
        }

        return new GraphFormatVersion(major, minor);
    }

    /// <summary>
    /// The caveat written into a saved graph that carries data this build did not
    /// model. It goes in the file, not only in a tool response, because the file
    /// outlives the session that wrote it and will be read by something that never
    /// saw the warning — including a human opening it in an editor.
    ///
    /// Returned as lines for a <c>.comment</c> array: the house JSON convention
    /// for prose that belongs to a document rather than to its data.
    /// </summary>
    /// <param name="fromVersions">
    /// Newer format versions the data was read from. Empty when the unmodelled
    /// vocabulary arrived from an extractor extending this version rather than
    /// from a later one — a different origin, the same hazard.
    /// </param>
    public static IReadOnlyList<string> ForeignDataComment(IReadOnlyList<string> fromVersions)
    {
        var lines = new List<string>
        {
            "This graph holds data the writer did not model, so it is not uniformly trustworthy."
        };

        lines.Add(fromVersions.Count > 0
            ? $"Some of it was read from format {string.Join(", ", fromVersions)}, which is newer than " +
              $"the {Current} writer that produced this file. Only data belonging to format {Current} " +
              "-- the version in formatVersion -- can be relied on."
            : $"It uses node or edge kinds outside format {Current}, so it came from an extractor " +
              $"extending the format. Only data belonging to format {Current} -- the version in " +
              "formatVersion -- can be relied on.");

        lines.Add(
            "Everything listed here was preserved exactly as it was read and never interpreted. " +
            "No query in this build reasons about it, so an empty result for one of these kinds " +
            "means 'not modelled here', not 'not present'.");

        lines.Add(
            "The listing covers node and edge kinds only. Node and edge properties are an open " +
            "dictionary with no fixed schema, so unmodelled properties on kinds this build does " +
            "know are preserved as well but cannot be enumerated.");

        return lines;
    }

    /// <summary>
    /// Decide what to do with the version a document carried. Pass null for a
    /// document with no stamp.
    /// </summary>
    public static GraphFormatAssessment Assess(GraphFormatVersion? version)
    {
        if (version is not { } v)
        {
            return new GraphFormatAssessment(null, true,
                $"Graph carries no {PropertyName}: it was written before the format was stamped. " +
                "It loads, but nothing verifies its node and edge vocabulary matches this build's. " +
                "Rebuild it to get a stamped graph.");
        }

        if (v.Major > Current.Major)
        {
            return new GraphFormatAssessment(v, false,
                $"Graph format {v} is newer than this build reads (writes {Current}). " +
                "A major version means existing fields changed meaning, so reading it would " +
                "produce confident wrong answers. Upgrade RazorGraph, or rebuild the graph.");
        }

        if (v.Major < Current.Major)
        {
            return new GraphFormatAssessment(v, true,
                $"Graph format {v} predates this build's major version ({Current}). " +
                "It loads, but fields this build expects may be absent or hold their older meaning. " +
                "Rebuild it when the answers matter.");
        }

        if (v.Minor > Current.Minor)
        {
            return new GraphFormatAssessment(v, true,
                $"Graph format {v} is a newer minor than this build ({Current}). " +
                "Minor versions are additive, so it loads, but it may carry node kinds, edge kinds " +
                "or properties this build does not model — those are preserved, not interpreted. " +
                "An empty result for a newer concept means 'not modelled here', not 'not present'.");
        }

        return new GraphFormatAssessment(v, true, null);
    }
}
