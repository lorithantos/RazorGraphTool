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
                Reach(seed.Id, maxDepth, reachedAtDepth);

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
    private void Reach(string seedId, int maxDepth, Dictionary<string, (GraphNode Node, int Depth)> reached)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal) { seedId };
        var queue = new Queue<(string Id, int Depth)>();
        queue.Enqueue((seedId, 0));

        while (queue.Count > 0)
        {
            var (id, depth) = queue.Dequeue();
            if (depth >= maxDepth) continue;

            foreach (var nextId in Steps(id))
            {
                if (!visited.Add(nextId)) continue;
                if (graph.GetNode(nextId) is not { } node) continue;

                var nextDepth = depth + 1;
                if (!reached.TryGetValue(nextId, out var existing) || nextDepth < existing.Depth)
                    reached[nextId] = (node, nextDepth);
                queue.Enqueue((nextId, nextDepth));
            }
        }
    }

    /// <summary>
    /// Where a call from this method can land: what it calls, and -- when it is
    /// an interface member -- every body that implements it. Implements edges
    /// point implementation → interface, so the dispatch step is an incoming
    /// edge read the other way, the same join EscapeEmitter.CallerEdges makes.
    /// </summary>
    private IEnumerable<string> Steps(string id)
    {
        foreach (var edge in graph.Outgoing(id))
            if (edge.Type == EdgeType.Calls) yield return edge.ToId;

        foreach (var edge in graph.Incoming(id))
            if (edge.Type == EdgeType.Implements) yield return edge.FromId;
    }
}
