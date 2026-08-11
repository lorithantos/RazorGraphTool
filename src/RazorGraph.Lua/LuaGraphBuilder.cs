namespace RazorGraph.Lua;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.Checks;
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
    string? StructuralCaveat,
    IReadOnlyList<string> SkippedVendorFiles,
    LuaCallReport Calls,
    IReadOnlyList<LuaFinding> Findings);

/// <summary>
/// What became of the call sites. Counted rather than listed, because the
/// unresolved ones are dominated by the standard library and by locals this
/// pass does not track — naming thousands of them would bury the graph's real
/// gaps under noise, which is the opposite of what reporting is for.
/// </summary>
/// <param name="InGraph">Calls linked to a function node — a real Calls edge.</param>
/// <param name="External">Calls into the host's API or another unit's module.</param>
/// <param name="Stdlib">Calls to Lua's own library and globals.</param>
/// <param name="Unresolved">Everything else: locals, parameters, dynamic dispatch.</param>
public sealed record LuaCallReport(int Total, int InGraph, int External, int Stdlib, int Unresolved);

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

    /// <summary>
    /// Graph vendor code — an SDK's own sample plugins, a bundled dependency —
    /// instead of dropping it. Off by default so the graph is the author's own
    /// work; included files carry vendor=true and a vendorReason so queries can
    /// still tell the tiers apart. Dropped files are always reported.
    /// </summary>
    public bool IncludeVendor { get; set; }

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

        var vendorSkips = new List<string>();

        foreach (var file in files)
        {
            var vendorReason = host.VendorReason(file);
            if (vendorReason is not null && !IncludeVendor)
            {
                vendorSkips.Add($"{file.RelativePath} ({vendorReason})");
                continue;
            }

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
            if (vendorReason is not null)
            {
                node.SetProperty("vendor", true);
                node.SetProperty("vendorReason", vendorReason);
            }
            if (file.LoadOrder is { } order) node.SetProperty("loadOrder", order);
            _graph.AddNode(node);
        }

        var functions = 0;

        // Function ids within each module, keyed by BOTH the name as declared and
        // its last segment. A module writes `function M.trim()` while a caller
        // writes `util.trim()` after requiring it: the declaration carries the
        // table it hangs off, the call carries the variable it was bound to, and
        // neither knows the other's spelling. The shared part is the final name.
        var functionsByModule = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);

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

                if (!functionsByModule.TryGetValue(moduleId, out var byName))
                    functionsByModule[moduleId] = byName = new Dictionary<string, string>(StringComparer.Ordinal);

                // First declaration wins for both spellings: a name declared twice
                // in one file is a redefinition, and the graph should not invent a
                // second target for it.
                byName.TryAdd(fn.Name, fnId);
                byName.TryAdd(LastSegment(fn.Name), fnId);
            }
        }

        var resolved = 0;
        var external = 0;
        var unresolved = new List<string>();

        // Per module: the variable a reference was bound to, and what it resolved
        // to. `local LrDialogs = import 'LrDialogs'` is what lets a later
        // LrDialogs.message() be attributed to the SDK rather than dropped.
        var bindingsByModule = new Dictionary<string, Dictionary<string, ModuleBinding>>(StringComparer.Ordinal);

        foreach (var decl in declarations)
        {
            var moduleId = moduleIdByPath[decl.File.FullPath];
            var externalNames = new List<string>();
            var unresolvedHere = new List<string>();

            if (!bindingsByModule.TryGetValue(moduleId, out var bindings))
                bindingsByModule[moduleId] = bindings = new Dictionary<string, ModuleBinding>(StringComparer.Ordinal);

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
                        if (reference.BoundTo is { Length: > 0 } inGraphName)
                            bindings[inGraphName] = new ModuleBinding(targetId, null);
                        break;

                    case ModuleResolution.InGraph(var path):
                        // Resolved to a real file that is not in this unit.
                        externalNames.Add(reference.Target ?? path);
                        external++;
                        if (reference.BoundTo is { Length: > 0 } outsideName)
                            bindings[outsideName] = new ModuleBinding(null, reference.Target ?? path);
                        break;

                    case ModuleResolution.External(var name):
                        externalNames.Add(name);
                        external++;
                        if (reference.BoundTo is { Length: > 0 } externalName)
                            bindings[externalName] = new ModuleBinding(null, name);
                        break;

                    case ModuleResolution.Unresolved(var reason):
                        var where = $"{decl.File.RelativePath}:{reference.Line}";
                        unresolvedHere.Add($"{reference.Mechanism} — {reason}");
                        unresolved.Add($"{where} {reference.Mechanism}({reference.Target ?? "<expression>"}) — {reason}");
                        break;
                }
            }

            var module = _graph.GetNode(moduleId)!;

            // Host-specific facts the builder cannot know without becoming
            // host-specific itself. Default implementation does nothing.
            host.Annotate(module, externalNames);

            if (externalNames.Count > 0)
                module.SetProperty("externalReferences", externalNames.Distinct(StringComparer.Ordinal).OrderBy(n => n, StringComparer.Ordinal).ToList());
            if (unresolvedHere.Count > 0)
                module.SetProperty("unresolvedReferences", unresolvedHere);
        }

        var callReport = AddCallEdges(declarations, moduleIdByPath, functionsByModule, bindingsByModule, host);

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

        var findings = RunRules(declarations, host);

        var report = new LuaBuildReport(
            host.Name, evidence ?? "host supplied by caller",
            moduleCount, functions, resolved, external,
            unresolved, parseFailures, caveat, vendorSkips, callReport, findings);

        return (_graph, report);
    }

    /// <summary>
    /// Run the language rules and whatever the host adds.
    ///
    /// Run during the BUILD rather than behind a separate command, so findings
    /// reach every surface the build already has — the same reason unbound
    /// shapes are reported here. A check nobody runs is not a check.
    ///
    /// A rule that throws is reported and skipped: one broken rule, especially a
    /// third-party one, must not cost the whole graph.
    /// </summary>
    private IReadOnlyList<LuaFinding> RunRules(List<LuaFileDeclarations> declarations, ILuaHost host)
    {
        var context = new LuaCheckContext(_graph, declarations, host);
        var findings = new List<LuaFinding>();

        foreach (var rule in LanguageRules.Concat(host.Rules))
        {
            try
            {
                findings.AddRange(rule.Check(context));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warning: rule '{rule.Id}' failed and was skipped: {ex.Message}");
            }
        }

        // Ordered so two runs of the same tree produce the same report: severity
        // first because that is the reading order, then by location.
        return findings
            .OrderBy(f => f.Severity)
            .ThenBy(f => f.File, StringComparer.Ordinal)
            .ThenBy(f => f.Line)
            .ThenBy(f => f.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// Rules that belong to Lua rather than to any host, so they run everywhere.
    /// A host chooses its dialect; it does not choose whether the dialect holds.
    /// </summary>
    private static readonly IReadOnlyList<ILuaRule> LanguageRules = [new DialectRule()];

    /// <summary>What a module reference was bound to: a module in this graph, or a name outside it.</summary>
    private sealed record ModuleBinding(string? InGraphModuleId, string? ExternalName);

    /// <summary>
    /// Link each call site to what it calls.
    ///
    /// Four things can be resolved, and everything else is honestly counted
    /// rather than guessed at:
    ///
    /// 1. A call through a bound module — <c>u.trim()</c> after
    ///    <c>local u = require 'util'</c> — becomes a Calls edge to that
    ///    module's function.
    /// 2. A call on a bound EXTERNAL module — <c>LrDialogs.message()</c> — is an
    ///    external API call, handed to the host. This is what makes a minimum
    ///    host version answerable from what the code USES rather than what it
    ///    imports.
    /// 3. A call naming a function declared in the same module.
    /// 4. Lua's own library and globals, recognised so they are not reported as
    ///    gaps: <c>ipairs</c> is not a missing edge.
    ///
    /// What is left is genuinely unresolvable here — locals, parameters, values
    /// returned from other calls — and is a count, not a finding.
    /// </summary>
    private LuaCallReport AddCallEdges(
        List<LuaFileDeclarations> declarations,
        Dictionary<string, string> moduleIdByPath,
        Dictionary<string, Dictionary<string, string>> functionsByModule,
        Dictionary<string, Dictionary<string, ModuleBinding>> bindingsByModule,
        ILuaHost host)
    {
        int total = 0, inGraph = 0, externalCalls = 0, stdlib = 0, unresolved = 0;

        // External calls gathered per node, so the host is asked once per node
        // rather than once per call.
        var externalByNode = new Dictionary<string, List<ExternalCall>>(StringComparer.Ordinal);

        void RecordExternal(string nodeId, ExternalCall call)
        {
            if (!externalByNode.TryGetValue(nodeId, out var list))
                externalByNode[nodeId] = list = new List<ExternalCall>();
            list.Add(call);
        }

        foreach (var decl in declarations)
        {
            var moduleId = moduleIdByPath[decl.File.FullPath];
            var localFunctions = functionsByModule.GetValueOrDefault(moduleId);
            var bindings = bindingsByModule.GetValueOrDefault(moduleId);

            foreach (var call in decl.Calls)
            {
                total++;

                // A call at file scope belongs to the module: Lua module bodies
                // run on load, so that is where it really happens.
                var fromId = moduleId;
                if (call.EnclosingFunction is { } enclosing && call.EnclosingLine is { } line)
                {
                    var candidate = $"{moduleId}.{enclosing}#{line}";
                    if (_graph.HasNode(candidate)) fromId = candidate;
                }

                if (call.Member is { Length: > 0 } member
                    && bindings is not null
                    && bindings.TryGetValue(call.Root, out var binding))
                {
                    if (binding.InGraphModuleId is { } targetModule
                        && functionsByModule.TryGetValue(targetModule, out var targetFunctions)
                        && targetFunctions.TryGetValue(member, out var targetId))
                    {
                        var edge = new GraphEdge { FromId = fromId, ToId = targetId, Type = EdgeType.Calls };
                        edge.Properties["line"] = call.Line;
                        edge.Properties["callee"] = call.Callee;
                        edge.Properties["form"] = call.Form;
                        _graph.AddEdge(edge);
                        inGraph++;
                        continue;
                    }

                    if (binding.ExternalName is { } externalModule)
                    {
                        RecordExternal(fromId, new ExternalCall(externalModule, member, call.Line));
                        externalCalls++;
                        continue;
                    }
                }

                // Declared in this module, called by the name it was declared
                // under (M.f()) or by its bare name (f()).
                if (localFunctions is not null && localFunctions.TryGetValue(call.Callee, out var sameModule))
                {
                    var edge = new GraphEdge { FromId = fromId, ToId = sameModule, Type = EdgeType.Calls };
                    edge.Properties["line"] = call.Line;
                    edge.Properties["callee"] = call.Callee;
                    edge.Properties["form"] = call.Form;
                    _graph.AddEdge(edge);
                    inGraph++;
                    continue;
                }

                if (IsStandardLibrary(call)) { stdlib++; continue; }

                unresolved++;
            }
        }

        foreach (var (nodeId, calls) in externalByNode)
        {
            var node = _graph.GetNode(nodeId);
            if (node is null) continue;

            host.AnnotateExternalCalls(node, calls);

            // Recorded on the node as well as handed to the host, so a graph from
            // a host with no opinion still says what a function reaches outside
            // itself.
            node.SetProperty("externalCalls", calls
                .Select(c => $"{c.Module}.{c.Function}")
                .Distinct(StringComparer.Ordinal)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToList());
        }

        // A module's external surface is everything its functions reach, so the
        // host can answer "what does this FILE require of me" in one place.
        foreach (var decl in declarations)
        {
            var moduleId = moduleIdByPath[decl.File.FullPath];
            var module = _graph.GetNode(moduleId);
            if (module is null) continue;

            var forModule = _graph.Edges
                .Where(e => e.Type == EdgeType.Contains && e.FromId == moduleId)
                .Select(e => e.ToId)
                .Append(moduleId)
                .Where(externalByNode.ContainsKey)
                .SelectMany(id => externalByNode[id])
                .ToList();

            if (forModule.Count > 0) host.AnnotateExternalCalls(module, forModule);
        }

        return new LuaCallReport(total, inGraph, externalCalls, stdlib, unresolved);
    }

    /// <summary>
    /// Lua's own library and base globals. Recognised so they are counted as
    /// resolved-elsewhere rather than reported as gaps — a graph claiming 4,000
    /// unresolved calls because ipairs is not a node would be describing the
    /// language, not the code.
    /// </summary>
    private static bool IsStandardLibrary(LuaCall call)
    {
        if (call.Form is "member" or "chain" or "method") return StandardModules.Contains(call.Root);
        return StandardGlobals.Contains(call.Root);
    }

    private static readonly HashSet<string> StandardModules = new(StringComparer.Ordinal)
    {
        "string", "table", "math", "io", "os", "coroutine", "debug", "package", "utf8", "bit", "bit32", "jit"
    };

    private static readonly HashSet<string> StandardGlobals = new(StringComparer.Ordinal)
    {
        "assert", "collectgarbage", "dofile", "error", "getfenv", "getmetatable", "ipairs", "load",
        "loadfile", "loadstring", "next", "pairs", "pcall", "print", "rawequal", "rawget", "rawlen",
        "rawset", "require", "select", "setfenv", "setmetatable", "tonumber", "tostring", "type",
        "unpack", "xpcall"
    };

    /// <summary>The final name in a dotted or colon-separated Lua name: M.util.trim -> trim.</summary>
    private static string LastSegment(string name)
    {
        var cut = name.LastIndexOfAny(['.', ':']);
        return cut >= 0 && cut < name.Length - 1 ? name[(cut + 1)..] : name;
    }
}
