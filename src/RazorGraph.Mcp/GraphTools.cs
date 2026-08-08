namespace RazorGraph.Mcp;

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Core.Serialization;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// MCP tool surface over RazorGraph. All results are compact JSON — the consumer
/// is a model, not a terminal. Search-style tools return the envelope
/// { returned, totalMatches, truncated, ... } so callers can detect capped results.
///
/// Every tool takes an optional graphId. Omitting it means "the graph I most
/// recently built or loaded", which is what a single-graph session wants; naming
/// one lets several graphs be queried in the same session.
/// </summary>
[McpServerToolType]
public sealed class GraphTools(GraphStore store)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private const string NodeTypeList =
        "Project, RazorPage, PageModel, ApiController, ControllerAction, PartialView, ViewComponent, Layout, " +
        "Service, ServiceInterface, ServiceImplementation, ViewModel, Class, Method, Property, Field, " +
        "ViewDataKey, Middleware, Route, HtmlElement, TagHelperInvocation, JavaScriptFile, CssFile";

    private const string GraphIdDescription =
        "Graph to query. Omit to use the most recently built or loaded graph.";

    private const string DirectionDescription =
        "outgoing (default, the authored edge direction), incoming (who points at this node), or both.";

    // ---- Building and managing graphs ---------------------------------------

    [McpServerTool(Name = "build_graph")]
    [Description("Build a code graph from one ASP.NET Core Razor project (.csproj), or one project inside a solution. Slow (compiles the project). To graph a whole solution with edges that cross project boundaries, use build_solution instead. Returns a summary with node/edge counts by type.")]
    public async Task<string> BuildGraph(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Project name inside the solution; required when path is a solution")] string? projectName = null,
        [Description("Id to file the result under. Defaults to the file name; reusing an id replaces that graph.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var isSolution = IsSolution(full);
        if (isSolution && string.IsNullOrWhiteSpace(projectName))
            throw new McpException(
                "projectName is required when building a single project from a solution. " +
                "To graph every project at once, call build_solution.");

        await using var builder = new GraphBuilder { IncludeVendorAssets = includeVendor };
        var graph = isSolution
            ? await builder.BuildFromSolutionAsync(full, projectName!, ct)
            : await builder.BuildFromProjectAsync(full, ct);

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries);
    }

    [McpServerTool(Name = "build_solution")]
    [Description("Build ONE graph spanning every project in a solution (.sln/.slnx). Unlike build_graph, calls and injections that cross project boundaries become real edges — this is what makes \"which tests cover this method\" and \"what breaks if I change this\" answerable. Adds Project nodes, per-node project attribution, and Covers edges from test methods to the code they exercise. Slower than build_graph: it compiles every project.")]
    public async Task<string> BuildSolution(
        [Description("Absolute path to a .sln or .slnx file")] string path,
        [Description("Id to file the result under. Defaults to the solution file name.")] string? graphId = null,
        [Description("Also graph vendor/minified client assets (dropped by default). Their nodes carry vendor=true and a vendorReason; useful when the bug lives inside a shipped bundle.")] bool includeVendor = false,
        [Description("Skip test projects: no test Method nodes, no Covers edges — a leaner navigation graph. Coverage tools refuse to answer against it rather than reporting everything uncovered. Default false, so the default graph answers every question.")] bool excludeTests = false,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");
        if (!IsSolution(full))
            throw new McpException($"Not a solution file: {full}. Use build_graph for a .csproj.");

        await using var builder = new GraphBuilder
        {
            IncludeVendorAssets = includeVendor,
            ExcludeTestProjects = excludeTests
        };
        var graph = await builder.BuildFromSolutionAllAsync(full, ct);

        return Summarize(store.Add(graph, full, graphId), builder.AssetSkipSummaries, builder.SkippedTestProjects);
    }

    [McpServerTool(Name = "load_graph")]
    [Description("Load a previously built graph from a JSON file (produced by save_graph or the RazorGraph CLI). Fast; adds it alongside any graphs already loaded.")]
    public async Task<string> LoadGraph(
        [Description("Path to a graph JSON file")] string path,
        [Description("Id to file the result under. Defaults to the file name.")] string? graphId = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        var graph = GraphSerializer.FromJson(await File.ReadAllTextAsync(full, ct));
        return Summarize(store.Add(graph, full, graphId));
    }

    [McpServerTool(Name = "save_graph")]
    [Description("Save a loaded graph to a JSON file so future sessions can load_graph instead of rebuilding.")]
    public async Task<string> SaveGraph(
        [Description("Output path for the graph JSON file")] string outputPath,
        [Description(GraphIdDescription)] string? graphId = null,
        CancellationToken ct = default)
    {
        var entry = store.Require(graphId);
        var full = Path.GetFullPath(outputPath);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        await File.WriteAllTextAsync(full, GraphSerializer.ToJson(entry.Graph), ct);
        return ToJson(new { saved = full, graphId = entry.Id, nodes = entry.Graph.Nodes.Count, edges = entry.Graph.Edges.Count });
    }

    [McpServerTool(Name = "list_graphs")]
    [Description("Every graph currently held by the server: id, source, when it was loaded, and its size. Cheap. The 'current' one is what tools use when graphId is omitted.")]
    public string ListGraphs()
    {
        var entries = store.List().Select(e => new
        {
            graphId = e.Id,
            source = e.Source,
            loadedAt = e.LoadedAt,
            nodes = e.Graph.Nodes.Count,
            edges = e.Graph.Edges.Count,
            isCurrent = string.Equals(e.Id, store.CurrentId, StringComparison.OrdinalIgnoreCase)
        }).ToList();

        return ToJson(new { returned = entries.Count, current = store.CurrentId, graphs = entries });
    }

    [McpServerTool(Name = "drop_graph")]
    [Description("Forget a loaded graph, freeing its memory. Returns dropped=false when no graph had that id.")]
    public string DropGraph([Description("Id of the graph to drop")] string graphId) =>
        ToJson(new { dropped = store.Remove(graphId), graphId });

    [McpServerTool(Name = "graph_summary")]
    [Description("Summary of one graph: source path and node/edge counts by type. Cheap orientation call.")]
    public string GraphSummary([Description(GraphIdDescription)] string? graphId = null) =>
        Summarize(store.Require(graphId));

    // ---- Querying ------------------------------------------------------------

    [McpServerTool(Name = "find_nodes")]
    [Description($"Find nodes by type, optionally filtered by case-insensitive name substring and by project. Valid node types: {NodeTypeList}. Check 'truncated' before concluding you have seen everything.")]
    public string FindNodes(
        [Description("Node type, e.g. RazorPage")] string nodeType,
        [Description("Case-insensitive name substring filter")] string? nameContains = null,
        [Description("Restrict to nodes from this project (solution graphs only)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var query = new GraphQuery(graph);
        var all = query.FindNodes(ParseNodeType(nodeType), nameContains).ToList();

        if (!string.IsNullOrWhiteSpace(project))
        {
            all = all.Where(n => string.Equals(n.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
                     .ToList();
        }

        var page = all.Take(Math.Max(1, limit)).Select(NodeSummary).ToList();
        return ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, nodes = page });
    }

    [McpServerTool(Name = "get_node")]
    [Description("Full details of one node by id — properties, labels, and all one-hop outgoing and incoming edges with their targets. Ids come from find_nodes / other queries.")]
    public string GetNode(
        [Description("Node id")] string id,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var node = graph.GetNode(id) ?? throw new McpException($"Node not found: {id}. Use find_nodes to discover ids.");

        var outgoing = graph.Outgoing(id).Select(e => new
        {
            type = e.Type.ToString(),
            to = e.ToId,
            toName = graph.GetNode(e.ToId)?.Name,
            toType = graph.GetNode(e.ToId)?.Type.ToString(),
            properties = e.Properties.Count > 0 ? e.Properties : null
        }).ToList();

        var incoming = graph.Incoming(id).Select(e => new
        {
            type = e.Type.ToString(),
            from = e.FromId,
            fromName = graph.GetNode(e.FromId)?.Name,
            fromType = graph.GetNode(e.FromId)?.Type.ToString(),
            properties = e.Properties.Count > 0 ? e.Properties : null
        }).ToList();

        return ToJson(new { node = NodeDetail(node), outgoing, incoming });
    }

    [McpServerTool(Name = "render_tree")]
    [Description("Render dependencies of a Razor page: layout, partials, sections, and components, traversed to depth 5.")]
    public string RenderTree(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, pageId);
        var query = new GraphQuery(graph);
        var items = query.GetRenderTree(pageId)
            .Select(t => new { edgeType = t.Edge.Type.ToString(), node = NodeSummary(t.Node) })
            .ToList();
        return ToJson(new { pageId, returned = items.Count, items });
    }

    [McpServerTool(Name = "page_context")]
    [Description("Backing infrastructure of a Razor page: its PageModel, ViewModel, and injected services. Only valid for RazorPage nodes.")]
    public string PageContext(
        [Description("Id of a RazorPage node")] string pageId,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, pageId);
        var context = new GraphQuery(graph).GetPageContext(pageId)
            ?? throw new McpException($"Node '{pageId}' is not a RazorPage; page_context only applies to RazorPage nodes.");

        return ToJson(new
        {
            page = NodeSummary(context.Page),
            pageModel = context.PageModel is null ? null : NodeSummary(context.PageModel),
            viewModel = context.ViewModel is null ? null : NodeSummary(context.ViewModel),
            injectedServices = context.InjectedServices.Select(NodeSummary).ToList()
        });
    }

    [McpServerTool(Name = "trace_data_flow")]
    [Description("Trace data-flow edges (Reads, Writes, BindsTo, Calls, InjectedInto, ReturnsView) from a node, breadth-first. Containment is followed for free, so starting from a class or PageModel reaches the calls its own methods make. Set direction=incoming to ask who reaches this node instead.")]
    public string TraceDataFlow(
        [Description("Starting node id")] string startId,
        [Description("Max traversal depth (default 3)")] int maxDepth = 3,
        [Description(DirectionDescription)] string? direction = null,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, startId);
        var dir = ParseDirection(direction);

        var items = new GraphQuery(graph).TraceDataFlow(startId, maxDepth, dir)
            .Select(t => new { depth = t.Depth, edgeType = t.Edge.Type.ToString(), node = NodeSummary(t.Node) })
            .ToList();
        return ToJson(new { startId, maxDepth, direction = dir.ToString(), returned = items.Count, items });
    }

    [McpServerTool(Name = "find_path")]
    [Description("Find a path between two nodes (BFS, first path found). Returns found=false when no path exists. direction=both treats the graph as undirected, which answers \"are these two related at all\".")]
    public string FindPath(
        [Description("Source node id")] string fromId,
        [Description("Target node id")] string toId,
        [Description(DirectionDescription)] string? direction = null,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, fromId);
        RequireNode(graph, toId);

        var path = graph.FindPath(fromId, toId, edgeFilter: null, ParseDirection(direction));
        if (path is null) return ToJson(new { found = false, edges = Array.Empty<object>() });

        var edges = path.Select(e => new
        {
            from = e.FromId,
            fromName = graph.GetNode(e.FromId)?.Name,
            type = e.Type.ToString(),
            to = e.ToId,
            toName = graph.GetNode(e.ToId)?.Name
        }).ToList();
        return ToJson(new { found = true, hops = edges.Count, edges });
    }

    [McpServerTool(Name = "find_server_to_js_mismatches")]
    [Description("Anti-pattern report: server-prepared data (ViewData/model properties) consumed by client-side JavaScript, including inline <script> blocks.")]
    public string FindServerToJsMismatches([Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var items = new GraphQuery(graph).FindServerToJsMismatches()
            .Select(m => new { server = NodeSummary(m.ServerNode), js = NodeSummary(m.JsNode), edgeType = m.Edge.Type.ToString() })
            .ToList();
        return ToJson(new { returned = items.Count, items });
    }

    // ---- Coverage ------------------------------------------------------------

    [McpServerTool(Name = "covering_tests")]
    [Description("Test methods that exercise a given production method, nearest first. Requires a graph built with build_solution — coverage edges cross a project boundary and cannot exist in a single-project graph.")]
    public string CoveringTests(
        [Description("Id of a Method node in production code")] string methodId,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, methodId);

        var items = RequireCoverage(() => new GraphQuery(graph).GetCoveringTests(methodId)
            .Select(t => new { depth = t.Depth, node = NodeSummary(t.Test) })
            .ToList());
        return ToJson(new { methodId, returned = items.Count, tests = items });
    }

    [McpServerTool(Name = "covered_methods")]
    [Description("Production methods a given test method exercises, nearest first. depth=1 means the test calls it directly.")]
    public string CoveredMethods(
        [Description("Id of a test Method node")] string testId,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        RequireNode(graph, testId);

        var items = RequireCoverage(() => new GraphQuery(graph).GetCoveredMethods(testId)
            .Select(t => new { depth = t.Depth, node = NodeSummary(t.Method) })
            .ToList());
        return ToJson(new { testId, returned = items.Count, methods = items });
    }

    [McpServerTool(Name = "uncovered_methods")]
    [Description("Methods that no test reaches. Pass a project to scope the question to one assembly, which is almost always what you want. This is reachability through the call graph, not runtime coverage — a method listed here is unreached by any test's call chain, including setup done in test-class lifecycle hooks.")]
    public string UncoveredMethods(
        [Description("Project name to restrict to (e.g. the production assembly)")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = RequireCoverage(() => new GraphQuery(graph).FindUncoveredMethods(project).ToList());
        var page = all.Take(Math.Max(1, limit)).Select(NodeSummary).ToList();
        return ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, methods = page });
    }

    /// <summary>
    /// The query layer refuses coverage questions a test-less graph cannot
    /// answer (built single-project, or with excludeTests). Re-shaped as an
    /// McpException so the caller sees the refusal, same as the escapes guard.
    /// </summary>
    private static T RequireCoverage<T>(Func<T> query)
    {
        try { return query(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "deep_methods")]
    [Description("Methods whose body nests control flow at least minDepth levels deep — the deep-nesting (christmas-tree) report, deepest first. bodyDepth is syntactic nesting stamped at build time; expression-bodied and flat methods never appear.")]
    public string DeepMethods(
        [Description("Minimum nesting depth to report (e.g. 4)")] int minDepth,
        [Description("Project name to restrict to")] string? project = null,
        [Description("Max nodes to return (default 50)")] int limit = 50,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        var all = new GraphQuery(graph).FindDeepMethods(minDepth, project).ToList();
        var page = all.Take(Math.Max(1, limit))
            .Select(m => new { depth = m.GetProperty<int>("bodyDepth"), node = NodeSummary(m) })
            .ToList();
        return ToJson(new { returned = page.Count, totalMatches = all.Count, truncated = all.Count > page.Count, methods = page });
    }

    private static readonly string[] EntryPointKinds =
    {
        "main", "pageHandler", "controllerAction", "eventHandler", "asyncVoid",
        "frameworkOverride", "frameworkInterface", "middleware", "callback"
    };

    private static readonly string[] EscapeCaveats =
    {
        "Throws inside BCL/out-of-solution code are invisible; a chain through framework code is not analyzed.",
        "Calls resolve to their statically bound target; overrides reached by virtual dispatch are not widened.",
        "Delegate registrations are followed one hop: a method group or lambda handed to out-of-solution code marks its target (or the lambda's containing method) as a 'callback' entry point. A delegate stored and forwarded further is not tracked, and local functions are not followed.",
        "A lambda's throws are attributed to its containing method as conditional — the container is the only node the lambda has.",
        "catch clauses with 'when' filters count as conditional handling (conditional=true), never as handling.",
        "Interface dispatch widens to in-solution implementations (any implementation's throws reach the caller); class virtual overrides are not widened.",
        "Boundary interception is catch-set matching: pipeline order and Map branches are not modeled, and an intercepted escape is still reported — disposition says shaped, not safe. Status-code results (404, ProblemDetails returns) are ordinary returns, not exceptions, and never appear here.",
        "Top-level-statement Main is not modeled as an entry point; minimal-API lambda endpoints are not entry points."
    };

    [McpServerTool(Name = "exception_escapes")]
    [Description("Throwing operations that can reach an application entry point (Main, page handler, controller action, event handler, async-void method, framework override, framework-interface implementation, or a callback registered with out-of-solution code) without passing through a catch that handles them — the 'what can crash this process' report, shallowest chain first. Precomputed at build time over static call chains. Blind spots come back as data in 'caveats': out-of-solution (BCL) throwers are invisible, virtual dispatch is not widened, delegate registrations are followed one hop only, and a catch filter counts as conditional handling, not handling.")]
    public string ExceptionEscapes(
        [Description("Restrict to one entry-point kind: main, pageHandler, controllerAction, eventHandler, asyncVoid, frameworkOverride, frameworkInterface, middleware, callback")] string? entryPointKind = null,
        [Description("Case-insensitive substring filter on the escaping exception type")] string? exceptionType = null,
        [Description("Restrict to entry points from this project")] string? project = null,
        [Description("Only escapes reaching this entry-point method node id (m:...)")] string? entryPointId = null,
        [Description("Max escapes to return (default 50)")] int limit = 50,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        if (entryPointKind != null && !EntryPointKinds.Contains(entryPointKind, StringComparer.Ordinal))
            throw new McpException(
                $"Unknown entryPointKind '{entryPointKind}'. Valid kinds: {string.Join(", ", EntryPointKinds)}.");

        var graph = store.Require(graphId).Graph;
        if (entryPointId != null) RequireNode(graph, entryPointId);

        // A graph saved before this analysis existed has neither stamps nor
        // edges; silence would read as "nothing escapes", which is a claim the
        // graph cannot back.
        if (!graph.Edges.Any(e => e.Type == EdgeType.Escapes)
            && !graph.NodesOfType(NodeType.Method).Any(n => n.GetProperty<string>("entryPointKind") != null))
            throw new McpException(
                "This graph predates exception-escape analysis. Rebuild it with build_graph or build_solution.");

        var all = new GraphQuery(graph)
            .FindEscapingExceptions(entryPointKind, exceptionType, project, entryPointId)
            .ToList();
        var page = all.Take(Math.Max(1, limit))
            .Select(e =>
            {
                var interceptedBy = e.Edge.GetProperty<List<string>>("interceptedBy");
                var interceptedConditionally = e.Edge.GetProperty<List<string>>("interceptedConditionallyBy");

                return new
                {
                    exceptionType = e.Edge.GetProperty<string>("exceptionType"),
                    conditional = e.Edge.GetProperty<bool>("conditional"),
                    depth = e.Edge.GetProperty<int>("depth"),
                    // A shaped response by design (ValidationException -> 400)
                    // reads differently from a raw 500; both stay reported.
                    disposition = interceptedBy is { Count: > 0 } ? "intercepted"
                        : interceptedConditionally is { Count: > 0 } ? "conditionallyIntercepted"
                        : null,
                    interceptedBy = ResolveBoundaries(graph, interceptedBy, interceptedConditionally),
                    entryPoint = new
                    {
                        node = NodeSummary(e.EntryPoint),
                        kind = e.EntryPoint.GetProperty<string>("entryPointKind")
                    },
                    thrownBy = NodeSummary(e.Thrower),
                    path = (e.Edge.GetProperty<List<string>>("path") ?? new List<string>())
                        .Select(id => new { id, name = graph.GetNode(id)?.Name })
                        .ToList()
                };
            })
            .ToList();

        return ToJson(new
        {
            returned = page.Count,
            totalMatches = all.Count,
            truncated = all.Count > page.Count,
            escapes = page,
            caveats = EscapeCaveats
        });
    }

    [McpServerTool(Name = "method_body_graph")]
    [Description("The graph INSIDE one method: control-flow basic blocks with branch edges, structural regions (try/finally, lifetimes), and every call site anchored to its block with line and guard depth (conditions/loops that must be entered for it to run). Call targets use the same m: ids as the main graph. Slow — compiles the project; a serialized graph is not enough.")]
    public async Task<string> MethodBodyGraph(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Method node id (m:...) whose body to graph")] string methodId,
        [Description("Project name inside the solution; narrows the compile when path is a solution")] string? projectName = null,
        CancellationToken ct = default)
    {
        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        if (IsSolution(full))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(full, ct: ct);
            else
                await roslyn.LoadSolutionAsync(full, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(full, ct);
        }

        var body = roslyn.GetMethodBodyGraph(methodId)
            ?? throw new McpException(
                $"No body graph for '{methodId}': id not found in the compilation, or the method has no body.");
        return ToJson(body);
    }

    [McpServerTool(Name = "method_body_diff")]
    [Description("Prove two method bodies flow-equivalent, or report precisely why not: bisimulation over control-flow blocks comparing operations, calls, canonicalized branch conditions (guard inversion folds away), and exception-region context. Conservative by design — a renamed local reports different; a semantically wrong change is never blessed. Compare a method against another in the same compilation (againstMethodId) or against a saved body-graph JSON (baselinePath). Slow — compiles the project.")]
    public async Task<string> MethodBodyDiff(
        [Description("Absolute path to a .csproj, .sln, or .slnx file")] string path,
        [Description("Method node id (m:...) — the right side of the comparison")] string methodId,
        [Description("Another method id in the same compilation to compare against (left side)")] string? againstMethodId = null,
        [Description("Body-graph JSON file saved earlier (left side)")] string? baselinePath = null,
        [Description("Project name inside the solution; narrows the compile when path is a solution")] string? projectName = null,
        CancellationToken ct = default)
    {
        if ((againstMethodId == null) == (baselinePath == null))
            throw new McpException("Exactly one of againstMethodId or baselinePath is required.");

        var full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new McpException($"File not found: {full}");

        RoslynExtractor.EnsureMsBuildRegistered();

        await using var roslyn = new RoslynExtractor();
        if (IsSolution(full))
        {
            if (string.IsNullOrWhiteSpace(projectName))
                await roslyn.LoadAllProjectsAsync(full, ct: ct);
            else
                await roslyn.LoadSolutionAsync(full, projectName, ct);
        }
        else
        {
            await roslyn.LoadProjectAsync(full, ct);
        }

        var right = roslyn.GetMethodBodyGraph(methodId)
            ?? throw new McpException($"No body graph for '{methodId}'.");

        BodyGraph left;
        if (againstMethodId != null)
        {
            left = roslyn.GetMethodBodyGraph(againstMethodId)
                ?? throw new McpException($"No body graph for '{againstMethodId}'.");
        }
        else
        {
            var baselineFull = Path.GetFullPath(baselinePath!);
            if (!File.Exists(baselineFull)) throw new McpException($"Baseline file not found: {baselineFull}");
            left = JsonSerializer.Deserialize<BodyGraph>(await File.ReadAllTextAsync(baselineFull, ct), Json)
                ?? throw new McpException($"Baseline file is not a body graph: {baselineFull}");
        }

        return ToJson(BodyGraphComparer.Compare(left, right));
    }

    // ---- Research ------------------------------------------------------------

    [McpServerTool(Name = "research")]
    [Description("Export a relevance-scored subgraph around focus nodes for deep analysis. Focus nodes score 1.0; reachable nodes score 1/(1+depth); nodes below threshold are dropped along with edges touching them. Errors if any focus id is unknown.")]
    public string Research(
        [Description("Focus node ids to research from")] string[] focusIds,
        [Description("Free-text label describing the research question")] string query = "",
        [Description("Max traversal depth from focus nodes (default 3)")] int depth = 3,
        [Description("Minimum relevance (1/(1+depth)) a node needs to be included (default 0)")] double threshold = 0.0,
        [Description(DirectionDescription)] string? direction = null,
        [Description(GraphIdDescription)] string? graphId = null)
    {
        var graph = store.Require(graphId).Graph;
        if (focusIds.Length == 0) throw new McpException("At least one focus node id is required.");

        var missing = focusIds.Where(id => !graph.HasNode(id)).ToList();
        if (missing.Count > 0)
            throw new McpException($"Focus node(s) not in graph: {string.Join(", ", missing)}. Use find_nodes to discover ids.");

        var dir = ParseDirection(direction);
        var relevance = focusIds.Distinct().ToDictionary(id => id, _ => 1.0);
        foreach (var id in relevance.Keys.ToList())
        {
            foreach (var (node, _, d) in graph.Traverse(id, edgeFilter: null, maxDepth: depth, direction: dir))
            {
                var score = 1.0 / (1 + d);
                if (!relevance.TryGetValue(node.Id, out var existing) || score > existing)
                    relevance[node.Id] = score;
            }
        }

        return GraphSerializer.ToResearchDocument(graph, relevance, query, threshold);
    }

    // ---- Helpers -------------------------------------------------------------

    private static bool IsSolution(string path) =>
        path.EndsWith(".sln", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase);

    private static object? ResolveBoundaries(
        CodeGraph graph, List<string>? firm, List<string>? conditional)
    {
        if (firm is not { Count: > 0 } && conditional is not { Count: > 0 }) return null;

        return (firm ?? new List<string>())
            .Select(id => new { id, name = graph.GetNode(id)?.Name, conditional = false })
            .Concat((conditional ?? new List<string>())
                .Select(id => new { id, name = graph.GetNode(id)?.Name, conditional = true }))
            .ToList();
    }

    private static string ToJson(object value) => JsonSerializer.Serialize(value, Json);

    private static string Summarize(
        GraphStore.GraphEntry entry,
        IReadOnlyList<string>? vendorSkips = null,
        IReadOnlyList<string>? testProjectSkips = null)
    {
        var graph = entry.Graph;
        var nodeCounts = graph.Nodes.GroupBy(n => n.Type)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());
        var edgeCounts = graph.Edges.GroupBy(e => e.Type)
            .OrderByDescending(g => g.Count())
            .ToDictionary(g => g.Key.ToString(), g => g.Count());

        var projects = graph.NodesOfType(NodeType.Project).Select(n => n.Name).OrderBy(n => n).ToList();

        return ToJson(new
        {
            graphId = entry.Id,
            source = entry.Source,
            nodes = graph.Nodes.Count,
            edges = graph.Edges.Count,
            projects = projects.Count > 0 ? projects : null,
            nodeCounts,
            edgeCounts,
            // Present only on build responses that dropped vendor assets: a
            // silent skip would read as "everything was graphed".
            skippedVendorAssets = vendorSkips is { Count: > 0 } ? vendorSkips : null,
            // Same contract for excluded test projects: this graph cannot
            // answer coverage questions, and its summary must say why.
            skippedTestProjects = testProjectSkips is { Count: > 0 } ? testProjectSkips : null
        });
    }

    private static object NodeSummary(GraphNode n) => new
    {
        id = n.Id,
        type = n.Type.ToString(),
        name = n.Name,
        project = n.GetProperty<string>("project"),
        filePath = n.FilePath,
        line = n.LineStart
    };

    private static object NodeDetail(GraphNode n) => new
    {
        id = n.Id,
        type = n.Type.ToString(),
        name = n.Name,
        filePath = n.FilePath,
        lineStart = n.LineStart,
        lineEnd = n.LineEnd,
        properties = n.Properties.Count > 0 ? n.Properties : null,
        labels = n.Labels.Count > 0 ? n.Labels : null
    };

    private static void RequireNode(CodeGraph graph, string id)
    {
        if (!graph.HasNode(id))
            throw new McpException($"Node not found: {id}. Use find_nodes to discover ids.");
    }

    private static NodeType ParseNodeType(string value) =>
        Enum.TryParse<NodeType>(value, ignoreCase: true, out var type) && type != NodeType.Unknown
            ? type
            : throw new McpException($"Unknown node type '{value}'. Valid types: {NodeTypeList}");

    private static TraversalDirection ParseDirection(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TraversalDirection.Outgoing
            : Enum.TryParse<TraversalDirection>(value.Trim(), ignoreCase: true, out var dir)
                ? dir
                : throw new McpException($"Unknown direction '{value}'. Valid values: outgoing, incoming, both.");
}
