namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// Emits the Escapes edges: locally-unhandled throws propagated caller-ward
/// over the Calls edges until they reach an entry point, with boundary
/// interception matched against the HTTP boundary catch sets.
/// </summary>
internal sealed class EscapeEmitter(CodeGraph graph)
{
    /// <summary>
    /// One escaping-exception fact during the sweep: the type's ancestor
    /// chain, whether the only handling seen so far was a filtered catch, and
    /// the first hop toward the origin — BFS parent pointers, so one
    /// representative shortest path is reconstructible per (method, type).
    /// </summary>
    private readonly record struct EscapeFact(
        IReadOnlyList<string> AncestorChain, bool Conditional, string? FirstHop);

    /// <summary>
    /// Propagates locally-unhandled throws caller-ward over the Calls edges,
    /// stopping where an edge's guard set handles the type, and emits an
    /// Escapes edge for every (entry point, exception type) the worklist
    /// reaches — the precomputed answer to "what can crash this process".
    /// Monotone on a finite lattice (methods × thrown types, conditional
    /// upgradeable to firm once), so indirect recursion terminates without
    /// special handling. Runs in project and solution builds alike: escapes
    /// need no project boundary, unlike coverage.
    /// </summary>
    internal void AddExceptionEscapeEdges(IReadOnlyDictionary<string, IReadOnlyList<ThrownType>> methodThrows)
    {
        var state = new Dictionary<string, Dictionary<string, EscapeFact>>(StringComparer.Ordinal);
        var queue = new Queue<string>();

        foreach (var (methodId, throws) in methodThrows)
        {
            if (!graph.HasNode(methodId)) continue;

            var facts = state[methodId] = new Dictionary<string, EscapeFact>(StringComparer.Ordinal);
            foreach (var thrown in throws)
                facts[thrown.Type] = new EscapeFact(thrown.AncestorChain, thrown.Conditional, FirstHop: null);
            queue.Enqueue(methodId);
        }

        while (queue.Count > 0)
        {
            var methodId = queue.Dequeue();
            var facts = state[methodId];

            foreach (var edge in CallerEdges(methodId))
            {
                var guardedBy = edge.GetProperty<List<string>>("guardedBy");
                var filteredBy = edge.GetProperty<List<string>>("filteredBy");
                var changed = false;

                foreach (var (type, fact) in facts)
                {
                    if (Handles(guardedBy, fact.AncestorChain)) continue;

                    var conditional = fact.Conditional || Handles(filteredBy, fact.AncestorChain);
                    if (!state.TryGetValue(edge.FromId, out var callerFacts))
                        callerFacts = state[edge.FromId] = new Dictionary<string, EscapeFact>(StringComparer.Ordinal);

                    if (callerFacts.TryGetValue(type, out var existing))
                    {
                        // Only a conditional→firm upgrade is new information.
                        if (!existing.Conditional || conditional) continue;
                    }

                    callerFacts[type] = new EscapeFact(fact.AncestorChain, conditional, methodId);
                    changed = true;
                }

                if (changed) queue.Enqueue(edge.FromId);
            }
        }

        // Boundary methods that deliberately absorb exceptions on the HTTP
        // pipeline. Escapes into HTTP-shaped entry points are matched against
        // them: intercepted means "shaped response, by design" — a
        // ValidationException becoming a 400 — while unmatched means a raw
        // 500. Statically the pipeline order and Map branches are unknowable,
        // so this is catch-set matching, and it never suppresses a report.
        var boundaries = graph.NodesOfType(NodeType.Method)
            .Select(n => (n.Id,
                Firm: n.GetProperty<List<string>>("boundaryCatches"),
                Filtered: n.GetProperty<List<string>>("boundaryCatchesFiltered")))
            .Where(b => b.Firm != null || b.Filtered != null)
            .ToList();

        foreach (var entry in graph.NodesOfType(NodeType.Method).ToList())
        {
            if (entry.GetProperty<string>("entryPointKind") is not { } kind) continue;
            if (!state.TryGetValue(entry.Id, out var facts)) continue;

            foreach (var (type, fact) in facts)
            {
                var path = ReconstructPath(entry.Id, type, fact, state);

                var edge = new GraphEdge
                {
                    FromId = path[0],
                    ToId = entry.Id,
                    Type = EdgeType.Escapes
                };
                edge.Properties["exceptionType"] = type;
                edge.Properties["depth"] = path.Count - 1;
                edge.Properties["path"] = path;
                if (fact.Conditional) edge.Properties["conditional"] = true;

                if (kind is "pageHandler" or "controllerAction" or "middleware")
                {
                    var firmHits = boundaries
                        .Where(b => b.Id != entry.Id && Handles(b.Firm, fact.AncestorChain))
                        .Select(b => b.Id)
                        .ToList();
                    var conditionalHits = boundaries
                        .Where(b => b.Id != entry.Id && !firmHits.Contains(b.Id)
                            && Handles(b.Filtered, fact.AncestorChain))
                        .Select(b => b.Id)
                        .ToList();
                    if (firmHits.Count > 0) edge.Properties["interceptedBy"] = firmHits;
                    if (conditionalHits.Count > 0) edge.Properties["interceptedConditionallyBy"] = conditionalHits;
                }

                graph.AddEdge(edge);
            }
        }

        static bool Handles(List<string>? guards, IReadOnlyList<string> ancestorChain) =>
            guards != null
            && (guards.Contains("*") || guards.Intersect(ancestorChain, StringComparer.Ordinal).Any());
    }

    /// <summary>
    /// The edges that bring an escaping exception to more callers: direct
    /// incoming Calls, plus incoming Calls of every interface method this one
    /// implements — the caller bound to the interface, but this body is what
    /// runs, so its throws are what arrive. Conservative across multiple
    /// implementations: any implementation's throws reach the caller.
    /// </summary>
    private IEnumerable<GraphEdge> CallerEdges(string methodId)
    {
        foreach (var edge in graph.Incoming(methodId))
            if (edge.Type == EdgeType.Calls) yield return edge;

        if (graph.GetNode(methodId)?.GetProperty<List<string>>("implementsMethods") is not { } interfaces)
            yield break;

        foreach (var interfaceMethodId in interfaces)
            foreach (var edge in graph.Incoming(interfaceMethodId))
                if (edge.Type == EdgeType.Calls) yield return edge;
    }

    /// <summary>
    /// Walks the first-hop pointers from an entry point back to the throw's
    /// origin and returns the path thrower-first. The seen-set is defensive:
    /// hop pointers are BFS parents and cannot cycle, but a truncated path
    /// beats an infinite loop if that invariant ever breaks.
    /// </summary>
    private static List<string> ReconstructPath(
        string entryId, string type, EscapeFact fact,
        Dictionary<string, Dictionary<string, EscapeFact>> state)
    {
        var path = new List<string> { entryId };
        var seen = new HashSet<string>(StringComparer.Ordinal) { entryId };

        var cursor = fact;
        while (cursor.FirstHop is { } next && seen.Add(next))
        {
            path.Add(next);
            cursor = state[next][type];
        }

        path.Reverse();
        return path;
    }
}
