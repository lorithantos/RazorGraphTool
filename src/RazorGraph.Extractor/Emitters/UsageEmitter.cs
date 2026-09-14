namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// Emits how code uses code: Calls edges with their guard context, Reads and
/// Writes edges from member accesses, and the callback entry-point stamps for
/// methods out-of-solution code holds a delegate to. Consumes the extraction
/// streams as parameters — this emitter never touches the workspace session.
/// </summary>
internal sealed class UsageEmitter(CodeGraph graph)
{
    /// <param name="typeIdByFullName">
    /// Resolves a type's full name to its node id, for the one shape of call
    /// whose near end is a type rather than a member: a call made in static
    /// initialization. The id prefix depends on how the type classified, so
    /// only the emitter that minted the type nodes can answer.
    /// </param>
    internal void AddCallEdges(IEnumerable<CallSiteInfo> callSites, Func<string, string?> typeIdByFullName)
    {
        // One edge per caller→callee pair because a caller invoking the same
        // method three times is one dependency, and the graph is read as
        // navigation rather than as a profile. The guard context of every site
        // rides along as the intersection across sites: an exception passes
        // this edge unless every site guards it, so a type missing from the
        // intersection is a possible escape — the conservative direction.
        foreach (var group in callSites.GroupBy(s => (s.FromId, s.FromTypeFullName, s.ToId)))
        {
            var (memberId, fromTypeName, toId) = group.Key;

            // Static initialization has no member to attribute to, so the near end
            // is a type name resolved through the map that owns the type ids.
            var fromId = memberId ?? (fromTypeName is not null ? typeIdByFullName(fromTypeName) : null);
            if (fromId is null) continue;

            // Calls into types the classifier skipped have no node to point at.
            if (!graph.HasNode(fromId) || !graph.HasNode(toId)) continue;

            var edge = new GraphEdge
            {
                FromId = fromId,
                ToId = toId,
                Type = EdgeType.Calls
            };

            var guardedBy = IntersectGuards(group.Select(s => s.GuardedBy));
            var filteredBy = IntersectGuards(group.Select(s => s.FilteredBy));
            if (guardedBy.Count > 0) edge.Properties["guardedBy"] = guardedBy;
            if (filteredBy.Count > 0) edge.Properties["filteredBy"] = filteredBy;

            // Marked only when the dependency exists purely through delegate
            // references, so consumers reading the graph as navigation can
            // tell "calls" from "may cause to run".
            if (group.All(s => s.IsDelegate)) edge.Properties["viaDelegate"] = true;

            graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// Emits Quotes edges: from the code that produces a string to the declared
    /// node whose name that string matches.
    ///
    /// MATCHING IS BY NAME, AND ONLY WHEN THE NAME IS UNIQUE in the solution.
    /// Both halves of that are load-bearing. A string matching nothing declared
    /// is dropped, which is what keeps a report about coupling from becoming an
    /// index of every message in the codebase. A string matching SEVERAL
    /// declarations is dropped too, and that rule was learned by running the
    /// first build of this: "Dispose" in the extractor matched the Dispose of
    /// every test class, "Main" matched a generated entry point, and 174 rows
    /// came back of which almost none were about anything. An edge to each match
    /// is not a conservative answer, it is an invented one -- the author meant a
    /// single thing, usually a framework member with no node at all. A name
    /// declared once is a claim the graph can actually support: rename that
    /// declaration and this string is wrong.
    ///
    /// Runs after every declaration is a node, and on a solution build that means
    /// the index spans projects -- which is the point. The question worth asking
    /// is whether a project names another project's vocabulary without
    /// referencing it, and no per-project analyzer can ask it: compiling the
    /// quoting project, the quoted names are not in scope. That absence is the
    /// seam working correctly, and it is exactly what blocks the check.
    /// </summary>
    internal void AddQuotesEdges(IEnumerable<QuotedName> quoted)
    {
        var uniqueByName = graph.Nodes
            .Where(Nameable)
            .GroupBy(n => n.Name, StringComparer.Ordinal)
            .Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single().Id, StringComparer.Ordinal);

        // One edge per (source, target, provenance). The same name typed twice in
        // one method is one coupling; a name that is both a nameof and a literal
        // in the same method is two facts, because only one of them will survive
        // a rename.
        var seen = new HashSet<(string From, string To, string Provenance)>();

        foreach (var quote in quoted)
        {
            if (!graph.HasNode(quote.FromId)) continue;
            if (!uniqueByName.TryGetValue(quote.Value, out var toId)) continue;

            // A declaration naming itself -- a property whose attribute repeats
            // its own name -- is not coupling between two places.
            if (toId == quote.FromId) continue;
            if (!seen.Add((quote.FromId, toId, quote.Provenance))) continue;

            var edge = new GraphEdge { FromId = quote.FromId, ToId = toId, Type = EdgeType.Quotes };
            edge.Properties["value"] = quote.Value;
            edge.Properties["provenance"] = quote.Provenance;
            edge.Properties["line"] = quote.Line;
            graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// Emits the literal index: one Literal node per DISTINCT value, and one
    /// Mentions edge per occurrence, from the member that produces the string to
    /// that node.
    ///
    /// This is the OTHER consumer of the same scanner stream AddQuotesEdges reads,
    /// and the split is the point. Quotes keeps only strings whose value equals a
    /// uniquely-declared name, which is what makes it a rename-safety report; every
    /// other string was thrown away, and those are most of them -- a config key, a
    /// file name, a route fragment, an ini section. "Where is arrivalTicks read" had
    /// no answer in the graph and fell back to grep, although the scanner had
    /// already found every occurrence.
    ///
    /// DEDUPLICATED BY VALUE, so the node is the value rather than a site: five
    /// methods naming "waves.ini" make one node with five incoming edges, and
    /// asking the node who mentions it is the whole query. That is why the node
    /// carries no filePath, line or project -- there is no single site to put
    /// there, exactly as with ExternalType. The per-site facts ride the edges.
    ///
    /// The id cannot be the text: values contain quotes, colons and newlines, and
    /// an id is compared and stored as an opaque key. A SHA-256 prefix is stable
    /// across builds and machines, which a hash-code is not.
    ///
    /// No length ceiling. The scanner's own three-character floor is reused and
    /// nothing else is filtered here: a long value makes an ugly node name and
    /// nothing worse, whereas a cap would be a rule about which strings deserve to
    /// be findable, and there is no honest place to draw it.
    /// </summary>
    internal void AddMentionsEdges(IEnumerable<QuotedName> quoted)
    {
        foreach (var quote in quoted)
        {
            // Strings from code the classifier skipped have no member to hang off.
            if (!graph.HasNode(quote.FromId)) continue;

            var literalId = LiteralId(quote.Value);
            if (!graph.HasNode(literalId))
            {
                var node = new GraphNode
                {
                    Id = literalId,
                    Type = NodeType.Literal,
                    Name = quote.Value
                };
                // Repeated in a property as well as in the name: Name is what a name
                // search matches, and a consumer reading the value should not have to
                // know that the display name happens to be load-bearing.
                node.SetProperty("value", quote.Value);
                graph.AddNode(node);
            }

            var edge = new GraphEdge { FromId = quote.FromId, ToId = literalId, Type = EdgeType.Mentions };
            edge.Properties["line"] = quote.Line;
            edge.Properties["provenance"] = quote.Provenance;
            graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// The node id for a value: "lit:" and the first 16 hex characters of its
    /// SHA-256. Not a security boundary -- 64 bits of a cryptographic digest is
    /// simply the cheapest collision-free-in-practice key for arbitrary text.
    /// </summary>
    private static string LiteralId(string value) =>
        "lit:" + Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))[..16];

    /// <summary>
    /// Nodes whose name a string could plausibly be naming. Constructors are
    /// excluded: ".ctor" is an id spelling, never something anyone types.
    /// </summary>
    /// <remarks>
    /// Literal is excluded and the exclusion is load-bearing, not tidiness. A Literal
    /// node's Name IS the string, so without this every value that matched a
    /// declaration would also match itself, the name would stop being unique, and the
    /// Quotes edge would silently disappear -- the literal index quietly deleting the
    /// report it was added beside. Emission order already keeps these nodes out of the
    /// index, and depending on order for that would be one reordering away from a
    /// wrong answer with no test near the change.
    /// </remarks>
    private static bool Nameable(GraphNode node) =>
        node.Type is not (NodeType.Project or NodeType.Parameter or NodeType.Literal)
        && !node.Name.StartsWith('.');

    internal void AddMemberAccessEdges(IEnumerable<MemberAccessInfo> accesses)
    {
        // One Reads and/or one Writes edge per accessor→member pair, matching
        // the one-edge-per-pair rule for calls: the graph is navigation, not
        // a profile.
        foreach (var group in accesses.GroupBy(a => (a.FromId, a.ToId)))
        {
            var (fromId, toId) = group.Key;

            // Accesses from or to nodes the classifier skipped have nothing to anchor to.
            if (!graph.HasNode(fromId) || !graph.HasNode(toId)) continue;

            if (group.Any(a => a.IsRead))
                graph.AddEdge(new GraphEdge { FromId = fromId, ToId = toId, Type = EdgeType.Reads });
            if (group.Any(a => a.IsWrite))
                graph.AddEdge(new GraphEdge { FromId = fromId, ToId = toId, Type = EdgeType.Writes });
        }
    }

    /// <summary>
    /// Stamps the callback entry points: methods out-of-solution code holds a
    /// delegate to. Runs after AddMethodNodes so an existing classification
    /// (an event-handler-shaped method registered to a framework event is
    /// both) keeps the more specific kind. Test methods are excluded for the
    /// same reason ClassifyEntryPoint excludes them — every Assert.Throws
    /// lambda hands out-of-solution, and the test host catches what tests
    /// throw.
    /// </summary>
    internal void AddCallbackEntryPoints(IEnumerable<string> callbackTargetIds)
    {
        foreach (var targetId in callbackTargetIds)
        {
            if (graph.GetNode(targetId) is not { } node) continue;
            if (node.GetProperty<bool>("isTest") || node.GetProperty<bool>("isTestLifecycle")) continue;
            if (node.GetProperty<string>("entryPointKind") == null)
                node.SetProperty("entryPointKind", "callback");
        }
    }

    /// <summary>
    /// Intersection of per-site guard sets, where "*" (untyped catch-all) is
    /// the universe: it constrains nothing, and only survives when every site
    /// has it. The intersection is by exact type name — two sites catching
    /// different bases both able to stop the same exception intersect to
    /// empty, which over-reports escapes rather than hiding one.
    /// </summary>
    private static List<string> IntersectGuards(IEnumerable<IReadOnlyList<string>> sites)
    {
        List<string>? intersection = null;
        var allCatchAll = true;

        foreach (var site in sites)
        {
            if (site.Contains("*")) continue;

            allCatchAll = false;
            intersection = intersection == null
                ? site.Distinct().ToList()
                : intersection.Intersect(site, StringComparer.Ordinal).ToList();
        }

        return allCatchAll ? new List<string> { "*" } : intersection ?? new List<string>();
    }
}
