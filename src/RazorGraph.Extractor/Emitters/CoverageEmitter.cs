namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;

/// <summary>
/// Emits the Covers edges linking each test method to the production code it
/// exercises, by call-graph reachability.
/// </summary>
internal sealed class CoverageEmitter(CodeGraph graph)
{
    /// <summary>
    /// Link each test method to the production code it exercises.
    ///
    /// Reachability is followed through Calls to the full closure, not a fixed
    /// horizon: coverage is a reachability claim, and a truncated closure turns
    /// "no test reaches this" into a statement about the cutoff rather than the
    /// code. The depth each node was reached at rides on the edge, so a consumer
    /// who wants direct exercise alone filters on depth instead of the graph
    /// being pre-truncated for everyone. Edges are only emitted across a project
    /// boundary, since a test calling its own helpers is not coverage of
    /// anything. maxDepth remains as a guardrail for callers that want one.
    ///
    /// Traversal seeds from each test and from its class's lifecycle hooks
    /// (isTestLifecycle) alike: the framework runs InitializeAsync/[SetUp]
    /// around every test in the class, so work done there is exercised by each
    /// of them even though no test calls it. A node reachable from both seeds
    /// keeps the shallower depth.
    ///
    /// Interface dispatch is widened the way the escape sweep widens it: a call
    /// bound to an interface member reaches every in-solution implementation
    /// one hop further on, via the method-level Implements edges. Without this,
    /// coverage parked on the interface member and every implementation reached
    /// only through it reported zero -- a confident "untested" for code that
    /// forty tests exercised, found on MVVM and DI-shaped code where dispatch
    /// through the interface is the ONLY way in. Conservative across multiple
    /// implementations, like reachability itself: any implementation the call
    /// could bind to is reached.
    ///
    /// The walk stands on properties and types as well as methods, because
    /// Calls and Constructs edges start at both: an accessor body's calls start
    /// at the property node, and a static initializer's at the type. A property
    /// is reached when anything the walk stands on Reads or Writes it, and its
    /// accessor bodies' edges are taken from there. A type is reached when the
    /// walk reaches one of its members or constructs it, and its static
    /// initialization's edges are taken from there -- touching a type runs its
    /// static initializer. Covers edges still land on methods alone: a property
    /// or a type is a place the walk passes through, not a thing a test covers.
    /// </summary>
    internal void AddCoverageEdges(int maxDepth = int.MaxValue)
    {
        var methods = graph.NodesOfType(NodeType.Method).ToList();

        var tests = methods.Where(n => n.GetProperty<bool>("isTest")).ToList();
        if (tests.Count == 0) return;

        var lifecycleByType = methods
            .Where(n => n.GetProperty<bool>("isTestLifecycle"))
            .GroupBy(n => n.GetProperty<string>("declaringType") ?? string.Empty)
            .ToDictionary(g => g.Key, g => g.ToList());

        foreach (var test in tests)
        {
            var testProject = test.GetProperty<string>("project");

            var seeds = new List<GraphNode> { test };
            var declaringType = test.GetProperty<string>("declaringType");
            if (declaringType != null && lifecycleByType.TryGetValue(declaringType, out var hooks))
                seeds.AddRange(hooks);

            // The walk completes before any edge is added: it reads straight off
            // the adjacency lists, and adding a Covers edge below mutates one of
            // them mid-enumeration.
            var reachedAtDepth = new Dictionary<string, (GraphNode Node, int Depth)>();
            foreach (var seed in seeds)
                Reach(seed, maxDepth, reachedAtDepth);

            foreach (var (node, depth) in reachedAtDepth.Values)
            {
                if (node.Type != NodeType.Method) continue;
                if (node.GetProperty<bool>("isTest")) continue;
                if (node.GetProperty<bool>("isTestLifecycle")) continue;

                var project = node.GetProperty<string>("project");
                if (project == null || string.Equals(project, testProject, StringComparison.OrdinalIgnoreCase))
                    continue;

                graph.AddEdge(new GraphEdge
                {
                    FromId = test.Id,
                    ToId = node.Id,
                    Type = EdgeType.Covers,
                    Properties = { ["depth"] = depth }
                });
            }
        }
    }

    /// <summary>
    /// Breadth-first from one seed over the steps a call can take, recording the
    /// shallowest depth each node is reached at across every seed so far.
    /// </summary>
    /// <remarks>
    /// A 0-1 breadth-first search rather than a plain one, because one step --
    /// a member up to its type -- costs no depth: a free step goes to the front
    /// of the frontier and a paid one to the back, so every node is still settled
    /// at its shallowest depth. A node is expanded again only when a shorter path
    /// to it turns up, and a queue entry left behind by the longer one is skipped.
    /// </remarks>
    private void Reach(GraphNode seed, int maxDepth, Dictionary<string, (GraphNode Node, int Depth)> reached)
    {
        var settled = new Dictionary<string, int>(StringComparer.Ordinal) { [seed.Id] = 0 };
        var frontier = new LinkedList<(GraphNode Node, int Depth)>();
        frontier.AddLast((seed, 0));

        while (frontier.First is { } first)
        {
            frontier.RemoveFirst();
            var (current, depth) = first.Value;
            if (depth > settled[current.Id]) continue;

            foreach (var (nextId, cost) in Steps(current))
            {
                var nextDepth = depth + cost;
                if (nextDepth > maxDepth) continue;
                if (settled.TryGetValue(nextId, out var known) && known <= nextDepth) continue;
                if (graph.GetNode(nextId) is not { } node) continue;

                settled[nextId] = nextDepth;
                if (!reached.TryGetValue(nextId, out var existing) || nextDepth < existing.Depth)
                    reached[nextId] = (node, nextDepth);

                if (cost == 0) frontier.AddFirst((node, nextDepth));
                else frontier.AddLast((node, nextDepth));
            }
        }
    }

    /// <summary>
    /// Where the walk can go from what it stands on, and what each step costs in
    /// depth: what a member calls, builds, reads or writes; every body that might
    /// run instead of a declaration something dispatches to; and, from a member,
    /// the type that declares it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// PROPERTIES. Reads and Writes are how code touches a property, and its
    /// accessor bodies' Calls and Constructs start at the property node, so a
    /// read or a write is the step onto it. Both accessors ride one node, so
    /// either step reaches the edges of both -- conservative, like dispatch. A
    /// read of an auto-property reaches a node with nowhere further to go, which
    /// is why this does not mark data holders covered: Covers land on methods
    /// alone, and only a body with a call in it leads to one.
    /// </para>
    /// <para>
    /// TYPES. Static initialization's Calls and Constructs start at the type,
    /// because a static constructor is not a node. Touching a type runs it, so
    /// the type is reached from any of its members -- the declaring type is the
    /// incoming Contains -- and by a Constructs edge. From a type only Calls and
    /// Constructs are taken: its outgoing Contains would mark every member of a
    /// class covered because a test reached one of them, and its incoming
    /// type-level Implements is refused by <see cref="Dispatches"/>.
    /// </para>
    /// <para>
    /// DEPTH. Every step costs one, as a call does, except a member up to its
    /// type, which costs nothing: the graph's traversals already follow
    /// containment without spending depth (GraphQuery.StructuralEdges), and here
    /// it is the same access seen from the declaring side -- the static
    /// initializer runs at the point the member is touched, so what it calls sits
    /// one step below that point, as it does when a test builds the type.
    /// </para>
    /// <para>
    /// DISPATCH. Both mechanisms, because a caller cannot tell them apart and
    /// neither can a test. Implements and Overrides edges both point body →
    /// declaration, so the dispatch step is an incoming edge read the other
    /// way; the same join EscapeEmitter.CallerEdges makes for interfaces.
    /// </para>
    /// <para>
    /// Overrides was the missing half until 2026-09-05, and its absence was not
    /// a smaller answer but a wrong one: a test calling an abstract declaration
    /// reached nothing, so every override in a solution reported zero tests.
    /// On a doctrine or strategy layer -- overrides all the way down -- that is
    /// the whole layer reading as untested while being the most exercised code
    /// in the repository.
    /// </para>
    /// <para>
    /// Conservative in the same direction as interface dispatch: a call to a
    /// base declaration reaches EVERY in-solution override of it, because
    /// reachability cannot say which one the runtime picked.
    /// </para>
    /// </remarks>
    private IEnumerable<(string Id, int Cost)> Steps(GraphNode node)
    {
        var isMember = node.Type is NodeType.Method or NodeType.Property or NodeType.Field;

        foreach (var edge in graph.Outgoing(node.Id))
        {
            if (edge.Type is EdgeType.Calls or EdgeType.Constructs) yield return (edge.ToId, 1);
            else if (isMember && edge.Type is EdgeType.Reads or EdgeType.Writes) yield return (edge.ToId, 1);
        }

        foreach (var edge in graph.Incoming(node.Id))
        {
            if ((edge.Type is EdgeType.Implements or EdgeType.Overrides) && Dispatches(edge)) yield return (edge.FromId, 1);
            else if (isMember && edge.Type == EdgeType.Contains) yield return (edge.FromId, 0);
        }
    }

    /// <summary>
    /// Whether an implementation edge describes DISPATCH — one body standing in
    /// for another — rather than one type declaring another as its interface.
    /// </summary>
    /// <remarks>
    /// Implements runs type → interface as well as member → member since
    /// 2026-09-11, and the two answer different questions. Following the
    /// type-level edge here would step from an interface TYPE to an implementing
    /// TYPE and mark the whole class covered because a test reached the
    /// interface — a claim no test made, arrived at by absorbing an edge that
    /// was added for something else.
    /// <para>
    /// LOAD-BEARING since the walk began standing on types. It was written as
    /// defensive, when Steps descended along Calls alone and a type id could
    /// never enter the walk; the note it carried said the accident would end the
    /// day Steps learned an edge that lands on a type, and the member-to-type
    /// step is that edge. A test calling I.M now stands on the interface type,
    /// and without this check every implementing type's static initialization
    /// would be marked exercised because a test reached the interface.
    /// </para>
    /// </remarks>
    private bool Dispatches(GraphEdge edge) =>
        graph.GetNode(edge.FromId)?.Type is NodeType.Method or NodeType.Property;
}
