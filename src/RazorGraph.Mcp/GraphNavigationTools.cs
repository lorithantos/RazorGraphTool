namespace RazorGraph.Mcp;

using System.ComponentModel;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;

/// <summary>
/// Tools that find nodes and follow edges: lookup, traversal, pathfinding, and
/// the research export built from what a traversal reaches.
/// </summary>
[McpServerToolType]
internal sealed class GraphNavigationTools(GraphStore store)
{
    [McpServerTool(Name = "find_nodes")]
    [Description($"Find nodes by type, by case-insensitive name substring, or both; optionally restricted to a project. Omit nodeType to search every kind by name, which is the right first call when you know a name but not what kind of thing carries it. At least one of nodeType or nameContains is required. Valid node types: {ToolArguments.NodeTypeList}. A graph written by a newer version or a non-C# extractor may also carry foreign kinds; those are selectable by the name graph_summary and other results show for them. An unrecognised type is refused with the foreign kinds this graph holds, rather than returning nothing. Check 'truncated' before concluding you have seen everything.")]
    public string FindNodes(
        [Description("Node type, e.g. RazorPage; or a foreign kind name this graph carries, e.g. luaModule. Omit to search every kind by nameContains.")] string? nodeType = null,
        [Description("Case-insensitive name substring filter. Required when nodeType is omitted.")] string? nameContains = null,
        [Description("Restrict to nodes from this project (solution graphs only)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var query = new GraphQuery(graph);

        // A missing REQUIRED argument used to fail inside the SDK's binding step,
        // before this body ran, as "An error occurred invoking 'find_nodes'" --
        // a message naming nothing a caller could correct. Both parameters are
        // optional now, and the one refusal left names both of them.
        var byType = !string.IsNullOrWhiteSpace(nodeType);
        var byName = !string.IsNullOrWhiteSpace(nameContains);
        if (!byType && !byName)
            throw new McpException("find_nodes needs at least one of nodeType or nameContains: a type to list, a name substring to search every kind for, or both.");

        var all = (byType
            ? query.FindNodes(ToolArguments.ResolveNodeKind(graph, nodeType!), nameContains)
            : query.FindNodesNamed(nameContains!)).ToList();

        if (!string.IsNullOrWhiteSpace(project))
        {
            all = all.Where(n => string.Equals(n.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
                     .ToList();
        }

        // One probe per call, so the stat cache spans the whole result set: a
        // 50-node page is usually a handful of distinct files.
        var freshness = new GraphFreshness(graph);

        var page = all.Take(Math.Max(1, limit)).Select(n => ToolResponses.NodeSummary(n, freshness)).ToList();
        return ToolResponses.ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, nodes = page });
    }

    [McpServerTool(Name = "get_node")]
    [Description("Full details of one node by id — properties, labels, and all one-hop outgoing and incoming edges with their targets. Ids come from find_nodes / other queries.")]
    public string GetNode(
        [Description("Node id")] string id,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var node = graph.GetNode(id) ?? throw new McpException($"Node not found: {id}. Use find_nodes to discover ids.");

        var outgoing = graph.Outgoing(id).Select(e => new
        {
            type = e.DisplayType,
            to = e.ToId,
            toName = graph.GetNode(e.ToId)?.Name,
            toType = graph.GetNode(e.ToId)?.DisplayType,
            properties = e.Properties.Count > 0 ? e.Properties : null
        }).ToList();

        var incoming = graph.Incoming(id).Select(e => new
        {
            type = e.DisplayType,
            from = e.FromId,
            fromName = graph.GetNode(e.FromId)?.Name,
            fromType = graph.GetNode(e.FromId)?.DisplayType,
            properties = e.Properties.Count > 0 ? e.Properties : null
        }).ToList();

        // The node a caller is about to open at lineStart..lineEnd is exactly the
        // one worth flagging, so get_node carries it even though it is one node.
        return ToolResponses.ToJson(new
        {
            node = ToolResponses.NodeDetail(node, new GraphFreshness(graph)),
            outgoing,
            incoming
        });
    }

    [McpServerTool(Name = "trace_data_flow")]
    [Description("Trace data-flow edges (Reads, Writes, BindsTo, Calls, InjectedInto, ReturnsView) from a node, breadth-first. Containment is followed for free, so starting from a class or PageModel reaches the calls its own methods make. Set direction=incoming to ask who reaches this node instead.")]
    public string TraceDataFlow(
        [Description("Starting node id")] string startId,
        [Description("Max traversal depth (default 3)")] int maxDepth = 3,
        [Description(ToolArguments.DirectionDescription)] string? direction = null,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, startId);
        var dir = ToolArguments.ParseDirection(direction);

        var items = new GraphQuery(graph).TraceDataFlow(startId, maxDepth, dir)
            .Select(t => new { depth = t.Depth, edgeType = t.Edge.DisplayType, node = ToolResponses.NodeSummary(t.Node) })
            .ToList();
        return ToolResponses.ToJson(new { startId, maxDepth, direction = dir.ToString(), returned = items.Count, items });
    }

    [McpServerTool(Name = "find_path")]
    [Description("Find a path between two nodes (BFS, first path found). Returns found=false when no path exists. direction=both treats the graph as undirected, which answers \"are these two related at all\".")]
    public string FindPath(
        [Description("Source node id")] string fromId,
        [Description("Target node id")] string toId,
        [Description(ToolArguments.DirectionDescription)] string? direction = null,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        ToolArguments.RequireNode(graph, fromId);
        ToolArguments.RequireNode(graph, toId);

        var path = graph.FindPath(fromId, toId, edgeFilter: null, ToolArguments.ParseDirection(direction));
        if (path is null) return ToolResponses.ToJson(new { found = false, edges = Array.Empty<object>() });

        var edges = path.Select(e => new
        {
            from = e.FromId,
            fromName = graph.GetNode(e.FromId)?.Name,
            type = e.DisplayType,
            to = e.ToId,
            toName = graph.GetNode(e.ToId)?.Name
        }).ToList();
        return ToolResponses.ToJson(new { found = true, hops = edges.Count, edges });
    }

    [McpServerTool(Name = "research")]
    [Description("Export a relevance-scored subgraph around focus nodes for deep analysis. Focus nodes score 1.0; reachable nodes score 1/(1+depth); nodes below threshold are dropped along with edges touching them. Errors if any focus id is unknown.")]
    public string Research(
        [Description("Focus node ids to research from")] string[] focusIds,
        [Description("Free-text label describing the research question")] string query = "",
        [Description("Max traversal depth from focus nodes (default 3)")] int depth = 3,
        [Description("Minimum relevance (1/(1+depth)) a node needs to be included (default 0)")] double threshold = 0.0,
        [Description(ToolArguments.DirectionDescription)] string? direction = null,
        [Description(ToolArguments.GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        if (focusIds.Length == 0) throw new McpException("At least one focus node id is required.");

        // Scoring lives in Core (GraphQuery.ComputeRelevance) — shared with
        // the CLI twin. Only the missing-focus policy is this front end's:
        // hard error, because a model that typo'd an id should be told, not
        // handed a quietly smaller subgraph.
        var (relevance, missing) = new GraphQuery(graph)
            .ComputeRelevance(focusIds, depth, ToolArguments.ParseDirection(direction));
        if (missing.Count > 0)
            throw new McpException($"Focus node(s) not in graph: {string.Join(", ", missing)}. Use find_nodes to discover ids.");

        return GraphSerializer.ToResearchDocument(graph, relevance, query, threshold);
    }
}
