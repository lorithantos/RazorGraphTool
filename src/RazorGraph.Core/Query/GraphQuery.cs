namespace RazorGraph.Core.Query;

using RazorGraph.Core.Graph;

/// <summary>
/// High-level query surface over a CodeGraph. Designed to return
/// small, relevant result sets for LLM consumption.
/// </summary>
public sealed class GraphQuery
{
    private readonly CodeGraph _graph;

    public GraphQuery(CodeGraph graph) => _graph = graph;

    /// <summary>
    /// Get a single node by its stable ID.
    /// </summary>
    public GraphNode? GetNode(string id) => _graph.GetNode(id);

    /// <summary>
    /// Find nodes by type, optionally filtered by name substring.
    /// </summary>
    internal IEnumerable<GraphNode> FindNodes(NodeType type, string? nameContains = null)
    {
        var query = _graph.NodesOfType(type);
        if (!string.IsNullOrWhiteSpace(nameContains))
            query = query.Where(n => n.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        return query;
    }

    /// <summary>
    /// Find nodes by kind NAME rather than by enum member, so a kind this build
    /// has no <see cref="NodeType"/> for is still selectable — a graph written by
    /// a newer version or a third-party extractor stays queryable by whoever is
    /// holding it, without waiting for a build that knows the kind.
    /// </summary>
    /// <remarks>
    /// Matching is on <see cref="GraphNode.DisplayType"/>, which is the same
    /// string every report and tool response shows, so a kind a caller read out
    /// of a result is a kind they can paste straight back in.
    /// </remarks>
    public IEnumerable<GraphNode> FindNodes(string kind, string? nameContains = null)
    {
        var query = _graph.Nodes.Where(n => string.Equals(n.DisplayType, kind, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(nameContains))
            query = query.Where(n => n.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));
        return query;
    }

    /// <summary>
    /// Find nodes of ANY kind by name substring. This is the right first call
    /// when a caller knows a name and not yet what kind of thing carries it:
    /// requiring a kind up front turns one question into a guess per kind, and
    /// a wrong first guess reads as "no such thing".
    /// </summary>
    public IEnumerable<GraphNode> FindNodesNamed(string nameContains) =>
        _graph.Nodes.Where(n => n.Name.Contains(nameContains, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Get all direct neighbors of a node via outgoing edges of given types.
    /// </summary>
    public IEnumerable<(GraphEdge Edge, GraphNode Target)> GetNeighbors(
        string nodeId,
        params EdgeType[] edgeTypes)
    {
        var filter = edgeTypes.Length > 0 ? new HashSet<EdgeType>(edgeTypes) : null;
        foreach (var edge in _graph.Outgoing(nodeId))
        {
            if (filter != null && !filter.Contains(edge.Type)) continue;
            var target = _graph.GetNode(edge.ToId);
            if (target != null) yield return (edge, target);
        }
    }

    /// <summary>
    /// Get all nodes that point TO this node via given edge types.
    /// </summary>
    internal IEnumerable<(GraphEdge Edge, GraphNode Source)> GetPredecessors(
        string nodeId,
        params EdgeType[] edgeTypes)
    {
        var filter = edgeTypes.Length > 0 ? new HashSet<EdgeType>(edgeTypes) : null;
        foreach (var edge in _graph.Incoming(nodeId))
        {
            if (filter != null && !filter.Contains(edge.Type)) continue;
            var source = _graph.GetNode(edge.FromId);
            if (source != null) yield return (edge, source);
        }
    }

    /// <summary>
    /// Edges that carry data or control between two places in the code.
    /// Contains is in the set but is <see cref="StructuralEdges">transparent</see>:
    /// call edges hang off Method nodes, so a trace that cannot descend from a
    /// class into its own methods reports nothing for every class-level node —
    /// which is exactly the node type callers start from.
    /// </summary>
    private static readonly HashSet<EdgeType> DataFlowEdges = new()
    {
        EdgeType.Reads, EdgeType.Writes, EdgeType.BindsTo,
        EdgeType.Calls, EdgeType.InjectedInto, EdgeType.ReturnsView,
        EdgeType.Contains
    };

    /// <summary>Followed without consuming depth; see CodeGraph.Traverse.</summary>
    private static readonly HashSet<EdgeType> StructuralEdges = new() { EdgeType.Contains };

    /// <summary>
    /// Trace data flow: find all nodes reachable from start via Reads/Writes/BindsTo/Calls edges,
    /// descending through containment for free.
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge, int Depth)> TraceDataFlow(
        string startId,
        int maxDepth = 3,
        TraversalDirection direction = TraversalDirection.Outgoing) =>
        _graph.Traverse(startId, DataFlowEdges, maxDepth, direction, StructuralEdges);

    /// <summary>
    /// Test methods that exercise the given production method, nearest first.
    /// Covers edges point test -> code, so this is an incoming lookup.
    /// </summary>
    public IEnumerable<(GraphNode Test, int Depth)> GetCoveringTests(string methodId)
    {
        RequireTestMethods("covering-tests");
        return _graph.Incoming(methodId)
            .Where(e => e.Type == EdgeType.Covers)
            .Select(e => (Test: _graph.GetNode(e.FromId), Depth: e.GetProperty<int>("depth")))
            .Where(t => t.Test != null)
            .Select(t => (t.Test!, t.Depth))
            .OrderBy(t => t.Depth);
    }

    /// <summary>
    /// Production methods a given test exercises, nearest first.
    /// </summary>
    public IEnumerable<(GraphNode Method, int Depth)> GetCoveredMethods(string testId)
    {
        RequireTestMethods("covered-methods");
        return _graph.Outgoing(testId)
            .Where(e => e.Type == EdgeType.Covers)
            .Select(e => (Method: _graph.GetNode(e.ToId), Depth: e.GetProperty<int>("depth")))
            .Where(t => t.Method != null)
            .Select(t => (t.Method!, t.Depth))
            .OrderBy(t => t.Depth);
    }

    /// <summary>
    /// Methods with no test reaching them. Restricted to a project when given,
    /// because "untested" only means something inside a production assembly —
    /// the test project's own helpers are not the subject of the question.
    ///
    /// Bodiless declarations are excluded. An interface member never carries a
    /// Covers edge — calls from a test bind to the implementation, not the
    /// declaration — so including them would report every interface in the
    /// solution as untested and make the whole report untrustworthy.
    /// </summary>
    public IEnumerable<GraphNode> FindUncoveredMethods(string? project = null)
    {
        RequireTestMethods("uncovered-methods");
        return _graph.NodesOfType(NodeType.Method)
            .Where(m => !m.GetProperty<bool>("isTest"))
            .Where(m => !m.GetProperty<bool>("isAbstract"))
            .Where(m => project == null ||
                        string.Equals(m.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
            .Where(m => !_graph.Incoming(m.Id).Any(e => e.Type == EdgeType.Covers));
    }

    /// <summary>
    /// One place code names a declared symbol with a string: who names it, what
    /// is named, the text, where, and in which form.
    /// </summary>
    public sealed record QuotedSymbol(
        GraphNode From, GraphNode To, string Value, string Provenance, int Line);

    /// <summary>
    /// Where code names a declared symbol with a string rather than referencing
    /// it. The coupling is real and the compiler cannot see it, so a rename
    /// breaks one side and nothing reports it until the behaviour is wrong.
    ///
    /// Two filters, both defaulting to the high-signal answer rather than the
    /// complete one. nameof is excluded because it survives a rename: it is the
    /// same coupling in the form that is not a latent break, and listing it
    /// beside the breakable forms makes the report read as noise. Same-project
    /// naming is excluded because a seam is the question — a project naming
    /// another project's vocabulary without referencing it is the case no
    /// per-compilation analyzer can even ask, since compiling the quoting
    /// project those names are not in scope.
    /// </summary>
    /// <param name="project">Restrict to strings produced by code in this project.</param>
    /// <param name="includeSafe">Include nameof, which a rename carries along.</param>
    /// <param name="sameProject">Include strings naming a symbol in their own project.</param>
    public IEnumerable<QuotedSymbol> FindQuotedSymbols(
        string? project = null, bool includeSafe = false, bool sameProject = false)
    {
        foreach (var edge in _graph.Edges.Where(e => e.Type == EdgeType.Quotes))
        {
            var provenance = edge.GetProperty<string>("provenance") ?? "literal";
            if (!includeSafe && provenance == "nameof") continue;

            if (_graph.GetNode(edge.FromId) is not { } from) continue;
            if (_graph.GetNode(edge.ToId) is not { } to) continue;

            var fromProject = from.GetProperty<string>("project");
            if (project != null && !string.Equals(fromProject, project, StringComparison.OrdinalIgnoreCase)) continue;

            if (!sameProject &&
                string.Equals(fromProject, to.GetProperty<string>("project"), StringComparison.OrdinalIgnoreCase))
                continue;

            yield return new QuotedSymbol(
                from, to, edge.GetProperty<string>("value") ?? to.Name, provenance, edge.GetProperty<int>("line"));
        }
    }

    /// <summary>
    /// One node whose declared visibility exceeds its observed reach, with the
    /// projects that do consume it — always its own assembly, or nothing.
    /// </summary>
    public sealed record ExcessVisibility(GraphNode Node, IReadOnlyList<string> ConsumedBy);

    /// <summary>
    /// WORK IN PROGRESS, AND DELIBERATELY AGGRESSIVE. Public nodes with no
    /// consumer outside their own assembly — candidates for internal, not a
    /// verdict. Read the false-positive list below before acting on a result.
    ///
    /// The question is "does anything outside this assembly use it", so test
    /// projects are excluded by default: a test reaching in is not a reason to
    /// stay public when InternalsVisibleTo exists. A project counts as a test
    /// project when any node in it is marked isTest, which is graph-derived
    /// rather than name-based.
    ///
    /// The closure is what makes this worth running at all. A type pinned public
    /// by appearing in the signature of a method that IS externally consumed is
    /// required-public even though nothing calls the type — so external need
    /// propagates along signature References edges and along containment, to a
    /// fixed point, before anything is reported. Without that the tool
    /// confidently recommends changes that do not compile.
    ///
    /// KNOWN FALSE POSITIVES, none of which the graph can see:
    ///   * reflection, DI registration by string or open generic, and
    ///     serialization all consume a type with no edge to prove it;
    ///   * a project published as a package is meant to have consumers outside
    ///     this solution, so "no consumer here" is its normal state, not a
    ///     finding — scope with <paramref name="project"/> accordingly;
    ///   * an interface member implemented across an assembly boundary, where
    ///     the binding is to the declaration rather than the implementation.
    /// Treat the result as a worklist to verify, and let the compiler have the
    /// final word.
    ///
    /// RESULTS ARE INTERDEPENDENT — apply the set, not a line. A public type
    /// being required does not pin its members, since a public method on a
    /// public class can still be narrowed; so a member and the types only that
    /// member exposes are reported together. Narrow all of them and it compiles;
    /// narrow the type while leaving the still-public method returning it, and
    /// it does not.
    /// </summary>
    /// <param name="project">Restrict to nodes declared in this project. Recommended: the answer means little across a whole solution.</param>
    /// <param name="includeTests">Count test projects as real consumers. Off by default; on, this mostly reports nothing.</param>
    /// <param name="includeMembers">
    /// Also report properties and fields. Off by default because they swamp the
    /// result without adding an action: measured on this repo's own extractor,
    /// 243 of 364 candidates were record properties, which are a type's shape
    /// rather than separately narrowable surface. Types and methods are the
    /// grain a person actually edits.
    /// </param>
    public IEnumerable<ExcessVisibility> FindExcessVisibility(
        string? project = null, bool includeTests = false, bool includeMembers = false)
    {
        var testProjects = includeTests
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : _graph.Nodes
                .Where(n => n.GetProperty<bool>("isTest"))
                .Select(n => n.GetProperty<string>("project"))
                .Where(p => p != null)
                .Select(p => p!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Containment both ways: a member's reach is its type's reach, and using
        // a member uses the type that holds it.
        var ownerOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var edge in _graph.Edges.Where(e => e.Type == EdgeType.Contains))
            ownerOf[edge.ToId] = edge.FromId;

        string? ProjectOf(string id) => _graph.GetNode(id)?.GetProperty<string>("project");

        // Seed: reached from another assembly that is not a test project.
        // Covers edges never count -- they exist only between tests and code.
        var required = new HashSet<string>(StringComparer.Ordinal);
        var consumers = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var edge in _graph.Edges)
        {
            if (edge.Type == EdgeType.Covers) continue;
            if (ProjectOf(edge.FromId) is not { } fromProject) continue;
            if (ProjectOf(edge.ToId) is not { } toProject) continue;

            if (!consumers.TryGetValue(edge.ToId, out var seen))
                consumers[edge.ToId] = seen = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            seen.Add(fromProject);

            if (string.Equals(fromProject, toProject, StringComparison.OrdinalIgnoreCase)) continue;
            if (testProjects.Contains(fromProject)) continue;

            required.Add(edge.ToId);
            if (ownerOf.TryGetValue(edge.ToId, out var owner)) required.Add(owner);
        }

        // What a member exposes: a method's return and parameter types
        // (signature=true) and a property's or field's declared type.
        var exposes = _graph.Edges
            .Where(e => e.Type == EdgeType.References)
            .Where(e => e.GetProperty<bool>("signature")
                        || _graph.GetNode(e.FromId)?.Type is NodeType.Property or NodeType.Field)
            .ToList();

        bool InProject(GraphNode n) => project == null ||
            string.Equals(n.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase);

        bool IsInterface(GraphNode n) => n.GetProperty<bool>("isInterface") || n.Type == NodeType.ServiceInterface;

        // The fixed point has two halves, and the second is what makes the result
        // compile when applied. First: anything a required member exposes, and
        // the type holding a required member, is required. Second: anything that
        // will STAY public once the report is applied -- a property of a required
        // type, an interface member, a member outside the requested project, a
        // member of a type not being reported -- pins what it exposes just as a
        // required member does, because the compiler will hold it to the same
        // accessibility rule. Measured on this repo before the second half: 24 of
        // 105 offered narrowings failed CS0050/51/53 for exactly this reason.
        List<GraphNode> reported;
        bool grew;
        do
        {
            grew = false;
            foreach (var edge in exposes)
                if (required.Contains(edge.FromId) && required.Add(edge.ToId))
                    grew = true;

            foreach (var (child, owner) in ownerOf)
                if (required.Contains(child) && required.Add(owner))
                    grew = true;

            reported = Report();
            var reportedIds = reported.Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

            foreach (var edge in exposes)
                if (StaysPublic(edge.FromId, reportedIds) && required.Add(edge.ToId))
                    grew = true;
        }
        while (grew);

        return reported
            .Select(n => new ExcessVisibility(
                n,
                consumers.TryGetValue(n.Id, out var seen) ? seen.ToList() : Array.Empty<string>()))
            .OrderBy(r => r.Node.DisplayType, StringComparer.Ordinal)
            .ThenBy(r => r.Node.Name, StringComparer.Ordinal);

        // Reachable from outside once the report is applied: declared public,
        // every type up the containment chain public and not itself narrowed.
        bool StaysPublic(string id, HashSet<string> reportedIds)
        {
            for (var cursor = id; ; )
            {
                if (reportedIds.Contains(cursor)) return false;
                if (_graph.GetNode(cursor) is not { } node || !node.GetProperty<bool>("isPublic")) return false;
                if (!ownerOf.TryGetValue(cursor, out var owner)) return true;
                cursor = owner;
            }
        }

        List<GraphNode> Report()
        {
            var candidates = _graph.Nodes
                .Where(n => n.GetProperty<bool>("isPublic"))
                .Where(InProject)
                .Where(n => !required.Contains(n.Id))
                .Where(n => includeMembers || IsDeclaredType(n) || n.Type == NodeType.Method)
                .ToList();

            // A member whose own type is already reported adds nothing: narrowing
            // the type narrows everything inside it, and listing both turns one
            // decision into forty lines of worklist.
            var reportedTypes = candidates.Where(IsDeclaredType).Select(n => n.Id).ToHashSet(StringComparer.Ordinal);

            // Two more member cases carry no action, learned by running the result
            // as an edit plan on this repo. A public member of a type that is not
            // itself public (a private nested record's synthesized constructor) is
            // already unreachable; and an interface member -- abstract or with a
            // default body -- takes no modifier at all, so the only narrowing
            // available is the interface's own, reported in its own right when
            // nothing external needs it.
            bool Narrowable(GraphNode member)
            {
                // An override's accessibility is the base declaration's to choose, and a
                // primary constructor is declared by the type header on the type's own
                // line: neither has a line of its own to narrow. 19 of the last 20 rows
                // offered on this repo were positional-record constructors.
                if (member.GetProperty<bool>("isOverride") || member.GetProperty<bool>("isPrimaryConstructor")) return false;
                if (!ownerOf.TryGetValue(member.Id, out var ownerId)) return true;
                if (reportedTypes.Contains(ownerId)) return false;
                if (_graph.GetNode(ownerId) is not { } owner) return true;
                if (!owner.GetProperty<bool>("isPublic")) return false;
                return !IsInterface(owner);
            }

            return candidates.Where(n => IsDeclaredType(n) || Narrowable(n)).ToList();
        }
    }

    /// <summary>
    /// A declared type rather than something living inside one. Listed by kind
    /// rather than inferred from containment, because Project nodes contain
    /// types and that would make every type look like a member.
    /// </summary>
    private static bool IsDeclaredType(GraphNode node) => node.Type is
        NodeType.Class or NodeType.PageModel or NodeType.ApiController or NodeType.ViewModel
        or NodeType.Service or NodeType.ServiceInterface or NodeType.ServiceImplementation
        or NodeType.Middleware or NodeType.ExternalType;

    /// <summary>
    /// The relevance map behind a research selection: focus nodes score 1.0,
    /// every node reachable within maxDepth scores 1/(1+depth), and when
    /// multiple focus nodes reach the same node the best (nearest) score
    /// wins. Unknown focus ids come back in MissingFocusIds rather than
    /// throwing: whether a missing focus is a warning (CLI) or a hard error
    /// (MCP) is front-end policy. The scoring rule itself must not fork per
    /// front end — it lived as two copies once, and they had already diverged
    /// on direction support when this method un-forked them.
    /// </summary>
    public (Dictionary<string, double> Relevance, List<string> MissingFocusIds) ComputeRelevance(
        IEnumerable<string> focusIds,
        int maxDepth,
        TraversalDirection direction = TraversalDirection.Outgoing)
    {
        var relevance = new Dictionary<string, double>();
        var missing = new List<string>();
        foreach (var id in focusIds.Distinct())
        {
            if (!_graph.HasNode(id)) { missing.Add(id); continue; }
            relevance[id] = 1.0;
        }

        foreach (var id in relevance.Keys.ToList())
        {
            foreach (var (node, _, depth) in _graph.Traverse(id, edgeFilter: null, maxDepth: maxDepth, direction: direction))
            {
                var score = 1.0 / (1 + depth);
                if (!relevance.TryGetValue(node.Id, out var existing) || score > existing)
                    relevance[node.Id] = score;
            }
        }

        return (relevance, missing);
    }

    /// <summary>
    /// A coverage question against a graph containing no test methods has no
    /// answer, not an empty one: every method would read as uncovered, which
    /// is the absence-of-data-as-finding trap. Refusing loudly is the same
    /// contract as the escapes guard in the MCP layer. Fires for graphs built
    /// single-project or with tests excluded (--no-tests / excludeTests).
    /// </summary>
    private void RequireTestMethods(string operation)
    {
        if (!_graph.NodesOfType(NodeType.Method).Any(m => m.GetProperty<bool>("isTest")))
            throw new InvalidOperationException(
                $"{operation} needs a graph containing test methods, and this graph has none — "
                + "built from a single project, or with tests excluded? Every method would read "
                + "as uncovered, so refusing to answer. Rebuild with build-solution, without --no-tests.");
    }

    /// <summary>
    /// Methods whose body nests control flow at least minDepth levels deep —
    /// the christmas-tree report, deepest first. bodyDepth is stamped at
    /// extraction time (see BodyGraphExtractor.NestingDepth); methods without
    /// the property are flat or bodiless and never match a minDepth above 0.
    /// </summary>
    public IEnumerable<GraphNode> FindDeepMethods(int minDepth, string? project = null) =>
        _graph.NodesOfType(NodeType.Method)
            .Where(m => m.GetProperty<int>("bodyDepth") >= minDepth)
            .Where(m => project == null ||
                        string.Equals(m.GetProperty<string>("project"), project, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.GetProperty<int>("bodyDepth"));

    /// <summary>
    /// Exception escapes, shallowest first: Escapes edges (precomputed at
    /// build time) resolved to their thrower and entry-point nodes. Every
    /// filter narrows; all default to everything. entryPointKind matches the
    /// entry node's stamp exactly; exceptionTypeContains is a case-insensitive
    /// substring of the escaping type.
    /// </summary>
    public IEnumerable<(GraphNode Thrower, GraphNode EntryPoint, GraphEdge Edge)> FindEscapingExceptions(
        string? entryPointKind = null,
        string? exceptionTypeContains = null,
        string? project = null,
        string? entryPointId = null) =>
        _graph.Edges
            .Where(e => e.Type == EdgeType.Escapes)
            .Where(e => entryPointId == null || e.ToId == entryPointId)
            .Where(e => exceptionTypeContains == null ||
                        (e.GetProperty<string>("exceptionType") ?? "")
                            .Contains(exceptionTypeContains, StringComparison.OrdinalIgnoreCase))
            .Select(e => (Thrower: _graph.GetNode(e.FromId), EntryPoint: _graph.GetNode(e.ToId), Edge: e))
            .Where(t => t.Thrower != null && t.EntryPoint != null)
            .Where(t => entryPointKind == null ||
                        string.Equals(t.EntryPoint!.GetProperty<string>("entryPointKind"), entryPointKind,
                            StringComparison.Ordinal))
            .Where(t => project == null ||
                        string.Equals(t.EntryPoint!.GetProperty<string>("project"), project,
                            StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Edge.GetProperty<int>("depth"))
            .ThenBy(t => t.EntryPoint!.Id, StringComparer.Ordinal)
            .ThenBy(t => t.Edge.GetProperty<string>("exceptionType"), StringComparer.Ordinal)
            .Select(t => (t.Thrower!, t.EntryPoint!, t.Edge));

    /// <summary>
    /// Find all render dependencies of a Razor page (layout, partials, sections, components).
    /// </summary>
    public IEnumerable<(GraphNode Node, GraphEdge Edge)> GetRenderTree(string pageId)
    {
        var filter = new HashSet<EdgeType>
        {
            EdgeType.UsesLayout, EdgeType.RendersPartial,
            EdgeType.RendersComponent, EdgeType.DefinesSection
        };
        return _graph.Traverse(pageId, filter, maxDepth: 5)
            .Select(t => (t.Node, t.Edge));
    }

    /// <summary>
    /// Find the PageModel, Services, and ViewModel for a given Razor page.
    /// </summary>
    public PageContext? GetPageContext(string pageId)
    {
        var page = _graph.GetNode(pageId);
        if (page == null || page.Type != NodeType.RazorPage) return null;

        var model = GetNeighbors(pageId, EdgeType.PageServedBy)
            .Select(n => n.Target)
            .FirstOrDefault(n => n.Type == NodeType.PageModel);

        // InjectedInto edges point service -> consumer, so services are predecessors.
        var services = model != null
            ? GetPredecessors(model.Id, EdgeType.InjectedInto).Select(n => n.Source).ToList()
            : new List<GraphNode>();

        var viewModel = page.GetProperty<string>("modelType");
        GraphNode? vmNode = null;
        if (!string.IsNullOrWhiteSpace(viewModel))
            vmNode = FindNodes(NodeType.ViewModel, viewModel).FirstOrDefault()
                  ?? FindNodes(NodeType.Class, viewModel).FirstOrDefault();

        return new PageContext(page, model, vmNode, services);
    }

    /// <summary>
    /// Detect anti-patterns: server-prepared data consumed by client JS.
    /// Returns nodes where ViewData/Model properties are set but read by JS file nodes.
    /// </summary>
    public IEnumerable<(GraphNode ServerNode, GraphNode JsNode, GraphEdge Edge)> FindServerToJsMismatches()
    {
        // JS files consuming server-prepared state by name: ViewData/data-*
        // keys, model reads, and element ids reached by literal selectors.
        foreach (var js in _graph.NodesOfType(NodeType.JavaScriptFile))
        {
            foreach (var edge in _graph.Incoming(js.Id))
            {
                if (edge.Type is EdgeType.ViewDataReadBy or EdgeType.Reads or EdgeType.DomSelectedBy)
                {
                    var source = _graph.GetNode(edge.FromId);
                    if (source != null) yield return (source, js, edge);
                }
            }
        }
    }
}

/// <summary>
/// Bundled context for a Razor page and its backing infrastructure.
/// </summary>
public sealed record PageContext(
    GraphNode Page,
    GraphNode? PageModel,
    GraphNode? ViewModel,
    IReadOnlyList<GraphNode> InjectedServices);
