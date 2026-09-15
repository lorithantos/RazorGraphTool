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
internal static class GraphFormat
{
    /// <summary>
    /// The version this build writes. 1.1 added the attribute vocabulary —
    /// ExternalType and Parameter nodes, DecoratedBy and Registers edges, and
    /// the argument payloads riding them. 1.2 added the document-level
    /// <c>builtAt</c> stamp behind per-node freshness. 1.3 added the literal
    /// index — Literal nodes and Mentions edges — and covers the Overrides edge,
    /// which was added between 1.2 and here without a bump of its own. 1.4 added
    /// the Constructs edge, type-level and property-level Implements edges, and
    /// the <c>interfaces</c> property on type nodes. 1.5 added <c>isStatic</c> on
    /// type nodes, and Constructs edges sourced at a TYPE rather than a member —
    /// which is what static initialization has to be attributed to, there being no
    /// member to blame. 1.6 added the XAML view layer — XamlView nodes and DataBinds
    /// edges — so a desktop app's view layer is in the graph at all. 1.7 scoped those
    /// edges by the DataType a template declares, which changes which members they
    /// resolve to, and added missingOnDataType. 1.8 put baseType on the service and
    /// view-model node kinds, which had never carried it, and which changes which
    /// members a binding resolves to through the inheritance walk. Everything up to
    /// 1.8 was additive, so an older reader loads any of those graphs with a caveat
    /// rather than a refusal, and a graph written without the stamp reads back as
    /// "cannot tell" rather than as fresh. 2.0 renamed <c>lineDocs</c> to
    /// <c>docsStart</c>, and is the first change that is not additive. Beside
    /// lineStart and lineEnd the old name read as a COUNT of documentation lines,
    /// and a consumer took it for one on first contact; the new name says what it
    /// is, the line the documentation begins on. A 1.x reader given a 2.0 graph
    /// would not merely miss something new: it would find no lineDocs, take the
    /// absence at face value, and report every node as undocumented — a
    /// well-formed answer, silently wrong, on a file that parsed perfectly. That is
    /// exactly the case the hard refusal on a newer major exists for. The old key
    /// is still READ, so a 1.x graph loads whole under this build and can still be
    /// diffed against a 2.0 one; only the writing of it stopped.
    /// </summary>
    /// <remarks>
    /// 1.4 is worth reading as two different kinds of addition. Constructs is a new
    /// KIND, which an older reader keeps verbatim as foreign data. The Implements
    /// edges are not: they are more edges of a type every reader already knows, so
    /// an older reader absorbs them silently into whatever it does with Implements.
    /// That is the riskier half of the two and the reason the bump is worth having
    /// — the caveat is the only thing that tells a reader the population changed
    /// under an existing name.
    /// <para>
    /// 1.5 carries that lesson a second time, and it is why the bump covers more
    /// than the new property. Type-sourced Constructs edges are not a new KIND — a
    /// reader already knows Constructs — but they are a new SHAPE, a near end that
    /// is a type where every other one is a member. A consumer assuming FromId is
    /// always a member would be wrong against a graph still calling itself 1.4, and
    /// two builds would both claim 1.4 while holding different populations. Version
    /// the shape, not only the vocabulary.
    /// </para>
    /// <para>
    /// WHAT THE MINOR RULE ACTUALLY BUYS, demonstrated rather than argued. Because
    /// a minor bump is additive only, a graph saved by an older build still loads
    /// whole into a newer one — so both can be held in the same server under
    /// different ids and DIFFED. That is how the 1.4 change was verified on a real
    /// corpus: save the old graph before the rotation, load it beside a freshly
    /// built one, and compare edge counts by type. Node count unchanged and every
    /// pre-existing edge type byte-identical is "additive only" measured instead of
    /// asserted, and it is a check no amount of fixture testing substitutes for.
    /// A major bump forfeits this, which is a real cost to weigh against one —
    /// and it is why 2.0, the first major bump, still reads the key it renamed:
    /// paying that cost was optional, and the alias is what kept the diff.
    /// </para>
    /// <para>
    /// New node and edge kinds are precisely what the MINOR rule below is for, and
    /// a reader that does not know them still loads them: ParseType falls back to
    /// Unknown plus a ForeignType for both, and the document records what it could
    /// not model. So the bump costs an older reader a caveat and buys it the truth,
    /// which is the trade the stamp exists to make. Overrides shipping unstamped is
    /// the case against leaving it: nothing broke, and nothing said anything either.
    /// </para>
    /// </remarks>
    public static readonly GraphFormatVersion Current = new(2, 0);

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
