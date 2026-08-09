namespace RazorGraph.Extractor;

using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Roslyn;

/// <summary>
/// Emits what Roslyn declared: symbol, method, and member nodes, and the
/// relationships among declarations — inheritance, method implements,
/// extension methods, member type references, and constructor injection.
/// Owns the registries those passes share: the name→id map, the
/// member-references dedup set, and the thrown-type records the escape sweep
/// consumes later.
/// </summary>
internal sealed class DeclarationEmitter(CodeGraph graph)
{
    /// <summary>
    /// Full thrown-type records (with ancestor chains) per method id, kept for
    /// the escape sweep. The node property carries only the type names; the
    /// chains are build-time working data, not graph data.
    /// </summary>
    private readonly Dictionary<string, IReadOnlyList<ThrownType>> _methodThrows = new(StringComparer.Ordinal);

    /// <summary>Type full name → its node id; see AddSymbolNode.</summary>
    private readonly Dictionary<string, string> _typeIdByFullName = new(StringComparer.Ordinal);

    /// <summary>Symbol ids whose member References edges are already emitted; see AddMemberTypeReferences.</summary>
    private readonly HashSet<string> _memberRefsEmitted = new(StringComparer.Ordinal);

    /// <summary>What each method can throw, keyed by method id — input to the escape sweep.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<ThrownType>> MethodThrows => _methodThrows;

    internal void AddSymbolNode(SymbolInfo sym)
    {
        var node = new GraphNode
        {
            Id = sym.Id,
            Type = sym.Type,
            Name = sym.Name,
            FilePath = sym.FilePath,
            LineStart = sym.LineStart,
            LineEnd = sym.LineEnd
        };

        node.SetProperty("fullName", sym.FullName);
        if (sym.Project != null) node.SetProperty("project", sym.Project);
        if (sym.BaseType != null) node.SetProperty("baseType", sym.BaseType);
        if (sym.GeneratedFrom != null) node.SetProperty("generatedFrom", sym.GeneratedFrom);
        StampGenerated(node);
        if (sym.Properties.Count > 0) node.SetProperty("properties", sym.Properties.Select(p => p.Name).ToList());
        if (sym.Methods.Count > 0) node.SetProperty("methods", sym.Methods.Select(m => m.Name).ToList());
        if (sym.InjectedServices.Count > 0) node.SetProperty("injectedServices", sym.InjectedServices);

        graph.AddNode(node);

        // Full name → node id, for resolving member declared types to nodes
        // later: the id prefix depends on how the type classified (type:, vm:,
        // pm:, ...), so a name alone cannot be turned into an id. First
        // declaration wins, matching the partial-class rule for methods.
        _typeIdByFullName.TryAdd(sym.FullName, sym.Id);
    }

    internal void AddMethodNodes(SymbolInfo sym)
    {
        foreach (var method in sym.MethodNodes)
        {
            // A partial class declared across two files yields the same method id
            // twice; the first declaration wins rather than duplicating the node.
            if (graph.HasNode(method.Id)) continue;

            var node = new GraphNode
            {
                Id = method.Id,
                Type = NodeType.Method,
                Name = method.Name,
                FilePath = method.FilePath ?? sym.FilePath,
                LineStart = method.LineStart
            };

            node.SetProperty("signature", method.Signature);
            node.SetProperty("returnType", method.ReturnType);
            node.SetProperty("declaringType", sym.FullName);
            if (sym.Project != null) node.SetProperty("project", sym.Project);
            if (method.IsAsync) node.SetProperty("isAsync", true);
            if (method.IsStatic) node.SetProperty("isStatic", true);
            if (method.IsTest) node.SetProperty("isTest", true);
            if (method.IsTestLifecycle) node.SetProperty("isTestLifecycle", true);
            if (method.IsAbstract) node.SetProperty("isAbstract", true);
            if (method.NestingDepth > 0) node.SetProperty("bodyDepth", method.NestingDepth);
            if (method.Throws.Count > 0)
            {
                // Names on the node for display; the full ancestor chains stay
                // in memory for the escape sweep — persisting them would bloat
                // every Method node for data only the build consumes.
                node.SetProperty("throws", method.Throws.Select(t => t.Type).ToList());
                _methodThrows[method.Id] = method.Throws;
            }
            if (method.EntryPointKind is { } entryPointKind)
                node.SetProperty("entryPointKind", entryPointKind);
            if (method.ImplementsIds.Count > 0)
                node.SetProperty("implementsMethods", method.ImplementsIds);
            if (method.BoundaryCatches.Count > 0)
                node.SetProperty("boundaryCatches", method.BoundaryCatches);
            if (method.BoundaryCatchesFiltered.Count > 0)
                node.SetProperty("boundaryCatchesFiltered", method.BoundaryCatchesFiltered);
            node.SetProperty("isPublic", method.IsPublic);
            StampGenerated(node);

            graph.AddNode(node);

            graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = method.Id,
                Type = EdgeType.Contains
            });
        }
    }

    internal void AddMemberNodes(SymbolInfo sym)
    {
        foreach (var member in sym.MemberNodes)
        {
            // Same partial-class rule as methods: first declaration wins.
            if (graph.HasNode(member.Id)) continue;

            var node = new GraphNode
            {
                Id = member.Id,
                Type = member.Kind,
                Name = member.Name,
                FilePath = member.FilePath ?? sym.FilePath,
                LineStart = member.LineStart
            };

            node.SetProperty("memberType", member.MemberType);
            node.SetProperty("declaringType", sym.FullName);
            if (sym.Project != null) node.SetProperty("project", sym.Project);
            if (member.IsStatic) node.SetProperty("isStatic", true);
            if (member.IsConst) node.SetProperty("isConst", true);
            if (member.IsReadOnly) node.SetProperty("isReadOnly", true);
            if (member.HasBindProperty) node.SetProperty("bindProperty", true);
            node.SetProperty("isPublic", member.IsPublic);
            StampGenerated(node);

            graph.AddNode(node);

            graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = member.Id,
                Type = EdgeType.Contains
            });
        }
    }

    /// <summary>
    /// References edges from each member to the in-solution types its declared
    /// type mentions. Runs in the second pass because the name→id map is only
    /// complete once every symbol node exists. This edge is what lets a DTO
    /// that appears only in signatures answer "who uses this type": incoming
    /// References from the properties and fields typed by it.
    /// </summary>
    internal void AddMemberTypeReferences(SymbolInfo sym)
    {
        // A partial class yields one SymbolInfo per declaration, each carrying
        // the FULL member list (symbol members, not declaration members) — the
        // same first-declaration-wins rule the node adders apply, or every
        // member References edge doubles. Found on RetirementCore, where the
        // MVVM toolkit's generator makes half the view models partial.
        if (!_memberRefsEmitted.Add(sym.Id)) return;

        foreach (var member in sym.MemberNodes)
        {
            foreach (var typeName in member.ReferencedTypeFullNames)
            {
                if (!_typeIdByFullName.TryGetValue(typeName, out var typeId)) continue;

                graph.AddEdge(new GraphEdge
                {
                    FromId = member.Id,
                    ToId = typeId,
                    Type = EdgeType.References
                });
            }
        }
    }

    /// <summary>
    /// A node whose final, #line-mapped position still lands in a .g.cs is
    /// generated scaffolding: nothing a reader can navigate to as authored
    /// code. Marked, not dropped — its semantics (calls, throws) are real.
    /// The rule is position-based on purpose: a @functions method authored in
    /// a .cshtml maps back to the .cshtml and is NOT marked, even though the
    /// compiler met it inside a generated file.
    /// </summary>
    private static void StampGenerated(GraphNode node)
    {
        if (node.FilePath?.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) == true)
            node.SetProperty("generated", true);
    }

    internal void AddInheritanceEdges(SymbolInfo sym)
    {
        if (string.IsNullOrWhiteSpace(sym.BaseType)) return;

        // Find base type node
        var baseNode = graph.Nodes.FirstOrDefault(n =>
            (n.Type == NodeType.PageModel || n.Type == NodeType.Class) &&
            n.GetProperty<string>("fullName") == sym.BaseType);

        if (baseNode != null)
        {
            graph.AddEdge(new GraphEdge
            {
                FromId = sym.Id,
                ToId = baseNode.Id,
                Type = EdgeType.Inherits
            });
        }
    }

    /// <summary>
    /// Method-level Implements edges, implementation → interface method. The
    /// type-level edge says the class implements the interface; this one says
    /// which body actually runs when a caller binds to the interface member —
    /// the join escape propagation crosses DI on.
    /// </summary>
    internal void AddMethodImplementsEdges(IReadOnlyList<SymbolInfo> symbols)
    {
        foreach (var sym in symbols)
            foreach (var method in sym.MethodNodes)
                foreach (var interfaceMethodId in method.ImplementsIds)
                {
                    if (!graph.HasNode(method.Id) || !graph.HasNode(interfaceMethodId)) continue;

                    graph.AddEdge(new GraphEdge
                    {
                        FromId = method.Id,
                        ToId = interfaceMethodId,
                        Type = EdgeType.Implements
                    });
                }
    }

    /// <summary>
    /// Emits Extends edges from extension methods to the in-solution types
    /// they extend. Runs after every symbol is a node — extensions routinely
    /// live in a different file (and project) from the type they serve. An
    /// extension on an out-of-solution type (string, IEnumerable) has no node
    /// to point at and simply carries its extendsType property.
    /// </summary>
    internal void AddExtensionEdges(IReadOnlyList<SymbolInfo> symbols)
    {
        var idByFullName = symbols
            .GroupBy(s => s.FullName, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);

        foreach (var sym in symbols)
            foreach (var method in sym.MethodNodes)
            {
                if (method.ExtendsTypeFullName is not { } extended) continue;
                if (graph.GetNode(method.Id) is { } node)
                    node.SetProperty("extendsType", extended);

                if (!idByFullName.TryGetValue(extended, out var typeId)) continue;
                if (!graph.HasNode(method.Id) || !graph.HasNode(typeId)) continue;

                graph.AddEdge(new GraphEdge
                {
                    FromId = method.Id,
                    ToId = typeId,
                    Type = EdgeType.Extends
                });
            }
    }

    internal void AddInjectionEdges(SymbolInfo sym)
    {
        foreach (var serviceType in sym.InjectedServices)
        {
            var serviceNode = FindServiceNode(serviceType);
            if (serviceNode != null)
            {
                graph.AddEdge(new GraphEdge
                {
                    FromId = serviceNode.Id,
                    ToId = sym.Id,
                    Type = EdgeType.InjectedInto
                });
            }
        }
    }

    private GraphNode? FindServiceNode(string typeName)
    {
        // Try exact match
        var exact = graph.Nodes.FirstOrDefault(n =>
            n.GetProperty<string>("fullName") == typeName);
        if (exact != null) return exact;

        // Try interface name (IImageService → ImageService)
        if (typeName.StartsWith("I") && typeName.Length > 1)
        {
            var implName = typeName[1..];
            return graph.Nodes.FirstOrDefault(n =>
                n.Type == NodeType.ServiceImplementation && n.Name == implName);
        }

        return null;
    }
}
