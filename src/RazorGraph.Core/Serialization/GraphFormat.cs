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
    /// diffed against a 2.0 one; only the writing of it stopped. 3.0 renamed
    /// <c>injectedServices</c> to <c>constructorDependencies</c> and changed its
    /// shape, from a list of type names to a list of classified entries, each a
    /// <c>type</c> and a <c>kind</c> -- interface, type, framework or value. The
    /// old name claimed DI injection for what is really the first public
    /// constructor's parameter types: measured on one corpus, 181 of its 286
    /// entries were primitives, framework types or collections, and a flat string
    /// list could not say which. Unlike 2.0 there is NO read alias for the old
    /// key, and that is deliberate. Graphs are rebuilt in seconds rather than
    /// archived, no intermediate consumer exists, and an older major already
    /// reports a caveat on load -- so a 2.x graph simply has no
    /// constructorDependencies, and Assess says why. 3.1 added the document-level
    /// <c>source</c>, what the graph was extracted from, so a graph saved and
    /// loaded back still names its solution rather than the file it passed
    /// through. Additive, as 1.2's builtAt was: a 3.0 reader ignores the key, and
    /// a 3.0 graph read here has no source and is named after its file.
    /// 3.2 resolves a constructor parameter typed as a constructed generic --
    /// <c>IRepository&lt;CatalogItem&gt;</c> -- to the open definition's node when
    /// the name as written matches nothing, and puts the written type on that
    /// edge as <c>closedType</c>. Additive in the 1.4 sense: no edge changed, but
    /// InjectedInto gained members, so a 3.1 reader absorbs more of an edge type
    /// it already knows and the caveat is what tells it the population moved.
    /// 3.3 added <c>docsEnd</c>, the last line of a node's documentation, beside
    /// docsStart. It cannot be derived as lineStart - 1 once an attribute list
    /// sits between comment and declaration. Additive, unlike 2.0's rename: a
    /// 3.2 graph read here has no docsEnd, which reads as "not known".
    /// 3.4 added the document-level <c>hostArchitecture</c>, <c>platform</c> and
    /// <c>platformFrom</c>, and <c>platformTarget</c> on Project nodes, because
    /// MSBuild takes Platform from the environment when nothing passes it, and two
    /// builds of the same source either side of a server restart put generated
    /// code under different obj paths with nothing in either graph to say why.
    /// Additive, as 3.1's source was: a 3.3 reader ignores the keys, and a 3.3
    /// graph read here has no stamp, which reads as "cannot tell" -- never as
    /// "projectDefault".
    /// 3.5 added the TextFile node kind: one node per non-C# text file in the
    /// source tree, carrying its <c>lines</c>, <c>extension</c> and <c>sizeBytes</c>,
    /// so grep's graph section answers for a .json or a .md and not only for C#.
    /// Additive, as 1.6's XamlView was: a new KIND with no edges, which a 3.4 reader
    /// keeps verbatim as foreign data, and a 3.4 graph read here has no text files,
    /// which reads as "not indexed" -- grep's disk section still finds every line.
    /// 3.6 added the document-level <c>unrestoredProjects</c>, the projects that had
    /// no project.assets.json at build time. RazorGraph never restores, and an
    /// unrestored test project's [Fact] does not bind, so the graph holds no test
    /// methods and a coverage query refuses long after the build that knew why; the
    /// refusal reads this to name the projects. Written as [] when every project was
    /// restored. Additive, as 3.4's stamps were: a 3.5 reader ignores the key, and a
    /// 3.5 graph read here has none, which reads as "not recorded" -- never as "all
    /// restored".
    /// 3.7 walks property and indexer accessor bodies and expression bodies, so
    /// Calls and Constructs edges can now start at a Property node: a method called
    /// only from a getter, or a type built only in one, had no incoming edge and
    /// read as dead. A new SHAPE of two known kinds, in 1.5's sense -- no kind or
    /// property was added, but a consumer assuming every Calls edge leaves a Method
    /// would be wrong against this graph, and the bump is what tells a 3.6 reader
    /// the population moved. Coverage and escape propagation do not step through a
    /// property node, so Covers and Escapes are unchanged by it.
    /// 3.8 emits an InjectedInto edge for each @inject directive in a .cshtml or
    /// .razor file, from the service's node to the RazorPage, PartialView or
    /// Layout node that declares it, carrying <c>via</c> ("@inject"), <c>name</c>,
    /// <c>declaredType</c>, <c>line</c> and <c>typeFrom</c>; and an @inject whose
    /// type has no node is recorded on that page as <c>unresolvedInjections</c>.
    /// Until now InjectedInto only ever ended at a type, from a constructor
    /// parameter. A new SHAPE of a known kind plus a new property, in 1.5's sense:
    /// a 3.7 reader keeps both, but one assuming every InjectedInto edge ends at a
    /// type would be wrong against this graph, and the bump is what says so.
    /// 3.9 widens the Covers walk onto properties and types: a test that reads or
    /// writes a property reaches what its accessor bodies call and build, and a
    /// test that reaches a member of a type, or builds it, reaches what the type's
    /// static initialization calls and builds -- property- and type-sourced edges
    /// the graph already held and the walk never took. Covers still
    /// end at methods only, and the depth rule is unchanged except that a member up
    /// to its type costs none. Additive in the 1.4 sense: no kind or property was
    /// added, but Covers gained members, so a 3.8 reader absorbs more of an edge
    /// type it already knows -- and a 3.8 graph read here answers coverage with the
    /// narrower walk, which is the population change the bump exists to name.
    /// Exception-escape propagation is unchanged and still stops at a property.
    /// 3.10 attributes an @inject in a _ViewImports.cshtml or _Imports.razor to
    /// every file it reaches rather than to the imports file. Razor applies those
    /// directives to each .cshtml or .razor file in the folder and below, so each
    /// of them now has the InjectedInto edge, or the unresolvedInjections entry,
    /// for the service it inherits; and InjectedInto edges onto _ViewImports and
    /// _Imports nodes no longer exist, an imports file being where a service is
    /// declared and never what receives it. Every @inject edge and entry gains
    /// <c>declaredIn</c>, the project-relative path of the file whose directive
    /// it was -- the page itself, or the imports file -- and <c>line</c> is that
    /// file's. Additive in the 3.8 sense: a new property, and more edges of a
    /// kind and shape a 3.9 reader already knows, which it absorbs silently; but
    /// some edges moved rather than appeared, so a 3.9 graph read here still has
    /// its imports-file edges and no inherited ones, which is the population
    /// change the bump exists to name.
    /// 3.11 joins a Blazor component with no C# of its own to the class it
    /// compiles into. Components carry no [RazorCompiledItem], and the #line
    /// vote that stood in for it found only the lines copied in from
    /// _Imports.razor, so the compiledInto edge left the _Imports node rather
    /// than the component's. The generated file's #pragma checksum now names the
    /// source, and an imports file no longer wins the vote for any class but its
    /// own. compiledInto edges moved, and the @inject edges an affected
    /// component inherits now read their type from its compiled property
    /// (<c>typeFrom</c> "generatedProperty") rather than the directive text. No
    /// kind, shape or property is new; the bump names the population change.
    /// 3.12 writes an indexer's parameter types into its id, as a method's id
    /// already carries them: <c>prop:T.this[int]</c> where it was
    /// <c>prop:T.this[]</c>. Every indexer is named this[], so two indexers on one
    /// type shared one id -- one node, the first indexer's, carried both getters'
    /// edges and the second had no node. Every indexer's id changes, not only an
    /// overloaded one's, so adding an overload never renames its neighbour; the
    /// node's name stays this[]. MINOR by the policy's own words: no existing
    /// field changed meaning -- id is still a node's unique identity, and is now
    /// actually unique -- and nothing was added that a reader must understand. A
    /// 3.11 reader loads a 3.12 graph whole and reads it correctly; what moved is
    /// the population, Property nodes and the edges touching them re-keyed under
    /// a new spelling, and the bump is what tells a reader holding a hand-written
    /// this[] id, or diffing against a 3.11 graph, why it no longer matches.
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
    public static readonly GraphFormatVersion Current = new(3, 12);

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
