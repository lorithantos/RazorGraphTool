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

    /// <summary>Attribute usages already emitted; see AddAttributeEdges for why the line, source and type arguments are in the key.</summary>
    private readonly HashSet<(string From, string Attribute, int? Line, string Target, string? Source, string? TypeArgs)> _attributeEdges = new();

    /// <summary>Attributes whose class did not bind, one line each; see UnresolvedAttributes.</summary>
    private readonly List<string> _unresolvedAttributes = new();

    /// <summary>The policy deciding which attributes' argument payloads are withheld; see AddAttributeEdges.</summary>
    internal Attributes.AttributePolicy Policy { get; set; } = Attributes.AttributePolicy.Default;

    /// <summary>What each method can throw, keyed by method id — input to the escape sweep.</summary>
    internal IReadOnlyDictionary<string, IReadOnlyList<ThrownType>> MethodThrows => _methodThrows;

    /// <summary>
    /// Attribute sites whose class could not be resolved — reported by the build
    /// rather than kept, because each one means the compilation had errors and
    /// every other answer from this graph is correspondingly less trustworthy.
    /// </summary>
    internal IReadOnlyList<string> UnresolvedAttributes => _unresolvedAttributes;

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

            // Decorated parameters only — a param: node exists because it is
            // decorated, so absence means undecorated, never unmodelled.
            // Inside the first-declaration-wins guard above, or a partial
            // class would double its parameter nodes.
            foreach (var parameter in method.Parameters)
            {
                var parameterNode = new GraphNode
                {
                    Id = parameter.Id,
                    Type = NodeType.Parameter,
                    Name = parameter.Name,
                    FilePath = method.FilePath ?? sym.FilePath,
                    LineStart = parameter.Line ?? method.LineStart
                };
                parameterNode.SetProperty("ordinal", parameter.Ordinal);
                parameterNode.SetProperty("parameterType", parameter.ParameterType);
                if (sym.Project != null) parameterNode.SetProperty("project", sym.Project);
                StampGenerated(parameterNode);

                graph.AddNode(parameterNode);

                graph.AddEdge(new GraphEdge
                {
                    FromId = method.Id,
                    ToId = parameter.Id,
                    Type = EdgeType.Contains
                });
            }
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

    /// <summary>
    /// Emits DecoratedBy edges from every decorated node to the attribute's type,
    /// minting an ExternalType node for attributes the solution does not declare.
    /// </summary>
    /// <remarks>
    /// Runs after every symbol is a node, for the same reason AddExtensionEdges
    /// does: an attribute declared in this solution must resolve to the node it
    /// already has, and that node may belong to a project loaded later.
    /// </remarks>
    internal void AddAttributeEdges(SymbolInfo sym)
    {
        AddAttributeEdges(sym.Id, sym.Attributes);
        foreach (var method in sym.MethodNodes)
        {
            AddAttributeEdges(method.Id, method.Attributes);
            foreach (var parameter in method.Parameters) AddAttributeEdges(parameter.Id, parameter.Attributes);
        }
        foreach (var member in sym.MemberNodes) AddAttributeEdges(member.Id, member.Attributes);
    }

    /// <summary>
    /// DecoratedBy edges for assembly/module attributes, hung off the proj:
    /// node — the assembly and the project are the same grain in this graph.
    /// Solution builds only, because only those have Project nodes; the shared
    /// HasNode guard makes a single-project call a no-op rather than an error.
    /// </summary>
    internal void AddProjectAttributeEdges(string projectId, IReadOnlyList<AttributeUsage> usages) =>
        AddAttributeEdges(projectId, usages);

    private void AddAttributeEdges(string fromId, IReadOnlyList<AttributeUsage> usages)
    {
        // A node the classifier skipped has nothing to decorate, the same guard
        // every other edge pass applies.
        if (usages.Count == 0 || !graph.HasNode(fromId)) return;

        foreach (var usage in usages)
        {
            if (usage.UnresolvedReason is { } reason)
            {
                _unresolvedAttributes.Add($"{fromId} line {usage.Line?.ToString() ?? "?"} — {reason}");
                continue;
            }

            // A partial class is classified once per declaration, so the same
            // decorated member arrives twice. Keying the dedup on the LINE and
            // the SOURCE as well as the pair is what separates that from a
            // genuine repeat: twenty [InlineData] on one method are twenty
            // different lines — or, combined as [InlineData(1), InlineData(2)],
            // one line with different arguments — and must all survive, while
            // the partial's duplicate is identical in every part of the key.
            // Type arguments are in the key too, because a combined
            // [Reg<IFoo>, Reg<IBar>] list is one line, one original-definition
            // name, and no argument list — the instantiation is all that
            // distinguishes the two usages.
            var typeArgsKey = usage.TypeArgs is { Count: > 0 } ta ? string.Join(",", ta) : null;
            if (!_attributeEdges.Add((fromId, usage.FullName, usage.Line, usage.Target, usage.Source, typeArgsKey))) continue;

            var toId = ResolveAttributeType(usage);
            var edge = new GraphEdge { FromId = fromId, ToId = toId, Type = EdgeType.DecoratedBy };
            if (usage.Line is { } line) edge.Properties["line"] = line;

            // Only when it is not the obvious one. An attribute on a method node
            // is a method attribute unless it says otherwise, and [return: ...]
            // is the case that has to say so; a Project node hosts both
            // assembly and module attributes, so those always say which.
            if (usage.Target is "return" or "assembly" or "module")
                edge.Properties["target"] = usage.Target;

            // Policy can withhold an attribute's argument payload ([InlineData]
            // test data is the measured case) — the edge, its line, and its
            // type arguments stay, and so does unresolvedArgs below: a build
            // error must never be configurable into silence.
            var withholdPayload = Policy.SuppressArgumentsFor.Contains(usage.FullName);

            if (!withholdPayload && usage.Args is { Count: > 0 }) edge.Properties["args"] = usage.Args;
            if (!withholdPayload && usage.Named is { Count: > 0 }) edge.Properties["named"] = usage.Named;
            if (usage.TypeArgs is { Count: > 0 }) edge.Properties["typeArgs"] = usage.TypeArgs;
            if (!withholdPayload && usage.Source is { } source) edge.Properties["source"] = source;
            if (usage.UnresolvedArgs is { Count: > 0 } failed)
            {
                // The failure signal is this property's PRESENCE — the value
                // slots hold null, never a sentinel, because any sentinel could
                // be a real string someone wrote.
                edge.Properties["unresolvedArgs"] = failed;
                _unresolvedAttributes.Add(
                    $"{fromId} line {usage.Line?.ToString() ?? "?"} — [{usage.Name}] argument(s) "
                    + $"{string.Join(", ", failed)} did not evaluate — the compilation has errors");
            }

            graph.AddEdge(edge);
            AddRegistersEdges(fromId, usage, toId);
        }
    }

    /// <summary>
    /// Emits Registers edges from the decorated node to each in-solution type
    /// the usage names via typeof(...) or a generic type argument — the types a
    /// framework constructs or consults with no call site anywhere, which would
    /// otherwise read as dead code.
    /// </summary>
    /// <remarks>
    /// From the DECORATED node, never from the attribute node: that node is one
    /// per attribute type by construction, and hanging registrations off it
    /// would collapse every registration in the solution onto one hub, losing
    /// which decorated node each serves. The attribute node id and line ride
    /// the edge so it joins its DecoratedBy sibling on (from, attribute, line).
    /// A typeof pointing outside the solution gets no edge — the fact already
    /// rides the DecoratedBy payload, and an edge to a node that cannot say
    /// what it is would add reachability without meaning.
    /// </remarks>
    private void AddRegistersEdges(string fromId, AttributeUsage usage, string attributeId)
    {
        if (usage.RegisteredTypeFullNames is not { Count: > 0 } names) return;

        foreach (var name in names)
        {
            if (!_typeIdByFullName.TryGetValue(name, out var targetId)) continue;

            var edge = new GraphEdge { FromId = fromId, ToId = targetId, Type = EdgeType.Registers };
            edge.Properties["attribute"] = attributeId;
            if (usage.Line is { } line) edge.Properties["line"] = line;
            graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// The node an attribute's type resolves to: the one the solution already
    /// declares, or an ExternalType minted on first sight.
    /// </summary>
    private string ResolveAttributeType(AttributeUsage usage)
    {
        if (_typeIdByFullName.TryGetValue(usage.FullName, out var declared)) return declared;

        var id = $"ext:{usage.FullName}";
        if (graph.HasNode(id)) return id;

        var node = new GraphNode { Id = id, Type = NodeType.ExternalType, Name = usage.Name };
        node.SetProperty("fullName", usage.FullName);
        if (usage.Assembly != null) node.SetProperty("assembly", usage.Assembly);
        node.SetProperty("external", true);
        graph.AddNode(node);
        return id;
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
