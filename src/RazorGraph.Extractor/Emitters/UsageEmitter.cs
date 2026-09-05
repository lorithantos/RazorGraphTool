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
    internal void AddCallEdges(IEnumerable<CallSiteInfo> callSites)
    {
        // One edge per caller→callee pair because a caller invoking the same
        // method three times is one dependency, and the graph is read as
        // navigation rather than as a profile. The guard context of every site
        // rides along as the intersection across sites: an exception passes
        // this edge unless every site guards it, so a type missing from the
        // intersection is a possible escape — the conservative direction.
        foreach (var group in callSites.GroupBy(s => (s.FromId, s.ToId)))
        {
            var (fromId, toId) = group.Key;

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
    /// Nodes whose name a string could plausibly be naming. Constructors are
    /// excluded: ".ctor" is an id spelling, never something anyone types.
    /// </summary>
    private static bool Nameable(GraphNode node) =>
        node.Type is not (NodeType.Project or NodeType.Parameter)
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
