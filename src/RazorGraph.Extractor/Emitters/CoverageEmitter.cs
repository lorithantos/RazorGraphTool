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

        var callsOnly = new HashSet<EdgeType> { EdgeType.Calls };

        foreach (var test in tests)
        {
            var testProject = test.GetProperty<string>("project");

            var seeds = new List<GraphNode> { test };
            var declaringType = test.GetProperty<string>("declaringType");
            if (declaringType != null && lifecycleByType.TryGetValue(declaringType, out var hooks))
                seeds.AddRange(hooks);

            // Materialised before edges are added: Traverse streams straight off
            // the adjacency lists, and adding a Covers edge below mutates one of
            // them mid-enumeration.
            var reachedAtDepth = new Dictionary<string, (GraphNode Node, int Depth)>();
            foreach (var seed in seeds)
            {
                foreach (var (node, _, depth) in graph.Traverse(seed.Id, callsOnly, maxDepth).ToList())
                {
                    if (!reachedAtDepth.TryGetValue(node.Id, out var existing) || depth < existing.Depth)
                        reachedAtDepth[node.Id] = (node, depth);
                }
            }

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
}
