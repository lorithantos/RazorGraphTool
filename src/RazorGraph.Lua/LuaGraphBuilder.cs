namespace RazorGraph.Lua;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Hosts;

/// <summary>
/// What a build produced besides the graph. Everything here is reported rather
/// than swallowed: a module graph that quietly drops references claims a
/// completeness it does not have, which is the absence-as-finding trap.
/// </summary>
public sealed record LuaBuildReport(
    string HostName,
    string HostEvidence,
    int Modules,
    int Functions,
    int ResolvedReferences,
    int ExternalReferences,
    IReadOnlyList<string> UnresolvedReferences,
    IReadOnlyList<string> ParseFailures,
    string? StructuralCaveat);

/// <summary>
/// Builds a CodeGraph from a tree of Lua source. Sibling of
/// <c>RazorGraph.Extractor.GraphBuilder</c> rather than a plug-in to it: that one
/// takes a .csproj and runs Roslyn unconditionally.
///
/// Vocabulary is hybrid. Functions are core Method nodes and containment is a
/// core Contains edge, so existing queries work on Lua immediately; only the
/// genuinely new concepts — a module, and a module reference — ride the foreign
/// kind path, which is why a saved Lua graph declares itself as coming from an
/// extractor extending the format.
/// </summary>
public sealed class LuaGraphBuilder
{
    public const string ModuleKind = "luaModule";
    public const string RequiresKind = "requires";

    private readonly CodeGraph _graph = new();

    public (CodeGraph Graph, LuaBuildReport Report) Build(string rootPath, ILuaHost? host = null, string? evidence = null)
    {
        var root = Path.GetFullPath(rootPath);
        if (host is null)
        {
            var detected = HostDetection.Detect(root);
            host = detected.Host;
            evidence = detected.Evidence;
        }

        var extractor = new LuaDeclarationExtractor(host);
        var files = host.Discover(root).ToList();

        // Module ids by full path, so a resolved reference can find its target.
        var moduleIdByPath = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var declarations = new List<LuaFileDeclarations>();
        var parseFailures = new List<string>();

        foreach (var file in files)
        {
            string source;
            try
            {
                source = File.ReadAllText(file.FullPath);
            }
            catch (IOException ex)
            {
                parseFailures.Add($"{file.RelativePath}: {ex.Message}");
                continue;
            }

            var decl = extractor.Extract(file, source);
            declarations.Add(decl);
            if (decl.ParseErrors.Count > 0)
                parseFailures.Add($"{file.RelativePath}: {string.Join("; ", decl.ParseErrors)}");

            var moduleName = host.ModuleNameFor(file);
            var id = $"mod:{moduleName}";
            moduleIdByPath[file.FullPath] = id;

            var node = new GraphNode
            {
                Id = id,
                Type = NodeType.Unknown,
                ForeignType = ModuleKind,
                Name = moduleName,
                FilePath = file.FullPath,
                LineStart = 1
            };
            node.SetProperty("host", host.Name);
            node.SetProperty("relativePath", file.RelativePath);
            if (file.LoadOrder is { } order) node.SetProperty("loadOrder", order);
            _graph.AddNode(node);
        }

        var functions = 0;
        foreach (var decl in declarations)
        {
            var moduleId = moduleIdByPath[decl.File.FullPath];
            foreach (var fn in decl.Functions)
            {
                // Core Method node: a Lua function IS what Method means, so
                // pretending otherwise would cost every existing query for nothing.
                var fnId = $"{moduleId}.{fn.Name}#{fn.LineStart}";
                var fnNode = new GraphNode
                {
                    Id = fnId,
                    Type = NodeType.Method,
                    Name = fn.Name,
                    FilePath = decl.File.FullPath,
                    LineStart = fn.LineStart,
                    LineEnd = fn.LineEnd
                };
                fnNode.SetProperty("form", fn.Form);
                fnNode.SetProperty("isMethod", fn.IsMethod);
                _graph.AddNode(fnNode);
                _graph.AddEdge(new GraphEdge { FromId = moduleId, ToId = fnId, Type = EdgeType.Contains });
                functions++;
            }
        }

        var resolved = 0;
        var external = 0;
        var unresolved = new List<string>();

        foreach (var decl in declarations)
        {
            var moduleId = moduleIdByPath[decl.File.FullPath];
            var externalNames = new List<string>();
            var unresolvedHere = new List<string>();

            foreach (var reference in decl.References)
            {
                switch (host.Resolve(reference.Target, decl.File))
                {
                    case ModuleResolution.InGraph(var path) when moduleIdByPath.TryGetValue(path, out var targetId):
                        var edge = new GraphEdge
                        {
                            FromId = moduleId,
                            ToId = targetId,
                            Type = EdgeType.Unknown,
                            ForeignType = RequiresKind
                        };
                        edge.Properties["mechanism"] = reference.Mechanism;
                        edge.Properties["line"] = reference.Line;
                        _graph.AddEdge(edge);
                        resolved++;
                        break;

                    case ModuleResolution.InGraph(var path):
                        // Resolved to a real file that is not in this unit.
                        externalNames.Add(reference.Target ?? path);
                        external++;
                        break;

                    case ModuleResolution.External(var name):
                        externalNames.Add(name);
                        external++;
                        break;

                    case ModuleResolution.Unresolved(var reason):
                        var where = $"{decl.File.RelativePath}:{reference.Line}";
                        unresolvedHere.Add($"{reference.Mechanism} — {reason}");
                        unresolved.Add($"{where} {reference.Mechanism}({reference.Target ?? "<expression>"}) — {reason}");
                        break;
                }
            }

            var module = _graph.GetNode(moduleId)!;
            if (externalNames.Count > 0)
                module.SetProperty("externalReferences", externalNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList());
            if (unresolvedHere.Count > 0)
                module.SetProperty("unresolvedReferences", unresolvedHere);
        }

        // A host with no module-reference mechanism produces a graph with no
        // module edges, which is CORRECT and reads exactly like a broken
        // extractor. Saying so is the difference between the two.
        var caveat = host.ReferenceSupport == ModuleReferenceSupport.None
            ? $"Host '{host.Name}' has no module-reference mechanism: files are loaded in a declared order and " +
              "coupled through a shared global namespace. The absence of module edges here is a property of the " +
              "host, not a gap in extraction, and file relationships are not statically determinable."
            : null;

        // Counted from the GRAPH, not from the file list. Two files that produce
        // the same module id are one node, and a report saying 75 over a graph
        // holding 32 hides exactly that collision.
        var moduleCount = _graph.Nodes.Count(n => n.ForeignType == ModuleKind);
        if (moduleCount != moduleIdByPath.Count)
        {
            parseFailures.Add(
                $"{moduleIdByPath.Count - moduleCount} file(s) collided onto an existing module id and were merged — " +
                "the host's module naming is not unique across this tree.");
        }

        var report = new LuaBuildReport(
            host.Name, evidence ?? "host supplied by caller",
            moduleCount, functions, resolved, external,
            unresolved, parseFailures, caveat);

        return (_graph, report);
    }
}
