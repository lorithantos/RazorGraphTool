namespace RazorGraph.Extractor;

using System.Text.Json;
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
            LineEnd = sym.LineEnd,
            DocsStart = sym.DocsStart,
            DocsEnd = sym.DocsEnd
        };

        node.SetProperty("fullName", sym.FullName);
        // Types carry isPublic for the same reason methods and members always
        // have: without it the graph cannot tell an exported type from an
        // internal one, and every question about a type's reach has to fall
        // back to reading source.
        node.SetProperty("isPublic", sym.IsPublic);
        // Always stamped, like isPublic: on a graph built before the flag existed the
        // property is absent, which is distinguishable from false.
        node.SetProperty("isInterface", sym.IsInterface);
        // Always stamped, like isInterface and for the same reason: on a graph
        // built before the flag existed the property is ABSENT, which a reader can
        // tell from false. A static class cannot be constructed, so its zero
        // incoming edges are a fact about C# rather than about the codebase.
        node.SetProperty("isStatic", sym.IsStatic);
        if (sym.Project != null) node.SetProperty("project", sym.Project);
        if (sym.BaseType != null) node.SetProperty("baseType", sym.BaseType);
        if (sym.GeneratedFrom != null) node.SetProperty("generatedFrom", sym.GeneratedFrom);
        StampGenerated(node);
        // Sorted by name: members come from GetMembers(), whose order across partial
        // declarations -- source-generated ones above all -- is not fixed from build to
        // build, and a list that reorders itself makes two graphs of one source differ.
        if (sym.Properties.Count > 0)
            node.SetProperty("properties", sym.Properties.Select(p => p.Name).Order(StringComparer.Ordinal).ToList());
        if (sym.Methods.Count > 0)
            node.SetProperty("methods", sym.Methods.Select(m => m.Name).Order(StringComparer.Ordinal).ToList());
        // One object per constructor parameter, kind as its camelCase enum name.
        // Stored as List<object> of Dictionary<string, object> rather than as the
        // records themselves, because that is exactly the shape the serializer
        // hands back on load -- so a graph reads the same either side of a save.
        if (sym.ConstructorDependencies.Count > 0)
            node.SetProperty("constructorDependencies", sym.ConstructorDependencies
                .Select(d => (object)new Dictionary<string, object>
                {
                    ["type"] = d.TypeFullName,
                    ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(d.Kind.ToString())
                })
                .ToList());
        // Every interface, including the out-of-solution ones that get no edge.
        // baseType alone said "object" for a class with a full interface list,
        // which is not wrong so much as the least informative true thing the
        // node could say about its own shape.
        if (sym.ImplementedInterfaces.Count > 0)
            node.SetProperty("interfaces", sym.ImplementedInterfaces.Select(i => i.FullName).ToList());

        graph.AddNode(node);

        // Full name → node id, for resolving member declared types to nodes
        // later: the id prefix depends on how the type classified (type:, vm:,
        // pm:, ...), so a name alone cannot be turned into an id. First
        // declaration wins, matching the partial-class rule for methods.
        _typeIdByFullName.TryAdd(sym.FullName, sym.Id);
    }

    /// <summary>
    /// The node id a declared type's full name resolves to, or null when the
    /// classifier declined the type. Handed to emitters that do not own the map
    /// but need one lookup from it — the Calls edge sourced at a type.
    /// </summary>
    internal string? TypeIdFor(string fullName) =>
        _typeIdByFullName.TryGetValue(fullName, out var id) ? id : null;

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
                LineStart = method.LineStart,
                LineEnd = method.LineEnd,
                DocsStart = method.DocsStart,
                DocsEnd = method.DocsEnd
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
            if (method.IsOverride) node.SetProperty("isOverride", true);
            if (method.IsPrimaryConstructor) node.SetProperty("isPrimaryConstructor", true);
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
            if (method.OverridesIds.Count > 0)
                node.SetProperty("overridesMethods", method.OverridesIds);
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
                LineStart = member.LineStart,
                LineEnd = member.LineEnd,
                DocsStart = member.DocsStart,
                DocsEnd = member.DocsEnd
            };

            node.SetProperty("memberType", member.MemberType);
            node.SetProperty("declaringType", sym.FullName);
            if (sym.Project != null) node.SetProperty("project", sym.Project);
            if (member.IsStatic) node.SetProperty("isStatic", true);
            if (member.IsConst) node.SetProperty("isConst", true);
            if (member.IsReadOnly) node.SetProperty("isReadOnly", true);
            if (member.IsOverride) node.SetProperty("isOverride", true);
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
    /// References edges from each member and method to the in-solution types
    /// its declaration mentions — a member's declared type, a method's return
    /// and parameter types. Runs in the second pass because the name→id map is
    /// only complete once every symbol node exists. These edges are what let a
    /// DTO that appears only in signatures answer "who uses this type": nothing
    /// calls it, and its incoming References are the whole story.
    /// </summary>
    internal void AddDeclaredTypeReferences(SymbolInfo sym)
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

        // Method signatures, marked so a consumer can tell "this type appears in
        // my contract" from "some member is declared as this type". C# pins a
        // signature type to at least the method's accessibility with no call
        // site anywhere, so this edge is the only record that the type cannot
        // be narrowed while the method stays as visible as it is.
        foreach (var method in sym.MethodNodes)
        {
            foreach (var typeName in method.SignatureTypeFullNames)
            {
                if (!_typeIdByFullName.TryGetValue(typeName, out var typeId)) continue;
                if (typeId == sym.Id) continue; // a method returning its own type says nothing

                var edge = new GraphEdge
                {
                    FromId = method.Id,
                    ToId = typeId,
                    Type = EdgeType.References
                };
                edge.Properties["signature"] = true;
                graph.AddEdge(edge);
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
    /// Constructs edges, from the member holding a new-expression to the type it
    /// builds, carrying the line and — when a member builds the same type more
    /// than once — how many sites there were.
    /// </summary>
    /// <remarks>
    /// Lives here rather than on UsageEmitter because it needs the name→id map,
    /// and grouped per (member, type) pair the way call edges are: a member that
    /// news up the same type twice is one relationship, and the count keeps the
    /// second site from being silently dropped.
    /// <para>
    /// Runs after every type node exists. An unresolvable name is skipped rather
    /// than minting an ext: node: a type outside the solution was already filtered
    /// at the scanner, so a miss here means the classifier declined the type, and
    /// inventing a node for it would put a type nothing declares into the graph.
    /// </para>
    /// </remarks>
    internal void AddConstructsEdges(IEnumerable<ConstructionSiteInfo> sites)
    {
        foreach (var group in sites.GroupBy(s => (s.FromId, s.FromTypeFullName, s.ToTypeFullName)))
        {
            var (memberId, fromTypeName, typeName) = group.Key;

            // Static initialization has no member to attribute to, so the near end
            // is a type name resolved through the same map as the far end.
            var fromId = memberId ?? (fromTypeName is not null
                && _typeIdByFullName.TryGetValue(fromTypeName, out var declaringId) ? declaringId : null);
            if (fromId is null) continue;

            if (!_typeIdByFullName.TryGetValue(typeName, out var toId)) continue;
            if (!graph.HasNode(fromId) || !graph.HasNode(toId)) continue;

            var edge = new GraphEdge
            {
                FromId = fromId,
                ToId = toId,
                Type = EdgeType.Constructs
            };
            edge.Properties["line"] = group.Min(s => s.Line);
            if (group.Count() > 1) edge.Properties["sites"] = group.Count();
            graph.AddEdge(edge);
        }
    }

    /// <summary>
    /// Type-level Implements edges, implementing type → interface, carrying the
    /// number of hops to the declaration that brought the interface in.
    /// </summary>
    /// <remarks>
    /// The edge <see cref="AddMethodImplementsEdges"/>'s own summary claimed
    /// existed and did not, until 2026-09-11. Its absence was not a smaller
    /// answer but a wrong one: asking an interface TYPE who implements it
    /// returned a well-formed zero, which is indistinguishable from nobody
    /// implementing it, and the real answer was one node away on the interface's
    /// METHOD. A reader who trusted the zero concluded the graph did not index
    /// implementation at all.
    /// <para>
    /// The SAME edge type as the method-level relation rather than a new one,
    /// which is the opposite of the call made for Overrides and Mentions. Those
    /// answer different QUESTIONS from the edge they were kept out of; this
    /// answers the same question at a different granularity, exactly as Contains
    /// runs type → method and type → property. The test is whether mixing them
    /// makes an existing answer wrong, and the only walk that reads Implements
    /// edges is CoverageEmitter.Steps, which now takes member-level edges only.
    /// </para>
    /// <para>
    /// Interface-to-interface is included: a derived interface reaches its base
    /// through the same walk, and since Inherits is driven by BaseType — null on
    /// an interface — nothing else in the graph records that relation at all.
    /// Callers wanting classes only can read isInterface on the far node.
    /// </para>
    /// </remarks>
    internal void AddTypeImplementsEdges(IReadOnlyList<SymbolInfo> symbols)
    {
        foreach (var sym in symbols)
            foreach (var iface in sym.ImplementedInterfaces)
            {
                // Out-of-solution interfaces have no node to point at. They stay
                // on the node's `interfaces` list, which is where a plain fact
                // about the declaration belongs; an edge needs two ends.
                if (!_typeIdByFullName.TryGetValue(iface.FullName, out var interfaceId)) continue;
                if (!graph.HasNode(sym.Id) || interfaceId == sym.Id) continue;

                var edge = new GraphEdge
                {
                    FromId = sym.Id,
                    ToId = interfaceId,
                    Type = EdgeType.Implements
                };
                edge.Properties["depth"] = iface.Depth;
                graph.AddEdge(edge);
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
    /// Member-level Implements edges for PROPERTIES, implementation → interface
    /// property. The same relation <see cref="AddMethodImplementsEdges"/> emits,
    /// for the members it could not see.
    /// </summary>
    /// <remarks>
    /// Its absence was the quietest of the three gaps closed on 2026-09-11,
    /// because it degraded by DEGREE: an interface with a method and a property
    /// still reported implementers — through the method — so the answer looked
    /// complete and was merely partial. Only an interface whose members are all
    /// properties failed outright, and it failed as a zero.
    /// <para>
    /// These edges answer "who implements this" and do NOT widen coverage, which
    /// is worth stating because the opposite is the natural assumption. The
    /// Covers walk steps along Calls edges, and reading a property produces a
    /// Reads edge, so a property node is never reached in the first place and
    /// there is nothing for the dispatch step to widen FROM. Making it reachable
    /// means following Reads in CoverageEmitter.Steps, which changes coverage for
    /// every property in every repository and wants its own evidence.
    /// </para>
    /// <para>
    /// Said here deliberately. The bug this change set exists to fix was a doc
    /// comment on AddMethodImplementsEdges asserting a type-level edge that was
    /// never built, and a comment claiming a widening that does not happen would
    /// be the same mistake one method down.
    /// </para>
    /// </remarks>
    internal void AddMemberImplementsEdges(IReadOnlyList<SymbolInfo> symbols)
    {
        foreach (var sym in symbols)
            foreach (var member in sym.MemberNodes)
                foreach (var interfaceMemberId in member.ImplementsIds)
                {
                    if (!graph.HasNode(member.Id) || !graph.HasNode(interfaceMemberId)) continue;

                    graph.AddEdge(new GraphEdge
                    {
                        FromId = member.Id,
                        ToId = interfaceMemberId,
                        Type = EdgeType.Implements
                    });
                }
    }

    /// <summary>
    /// Method-level Overrides edges, override → the declaration it replaces.
    /// The class-dispatch twin of <see cref="AddMethodImplementsEdges"/>, and
    /// the join coverage was missing: a caller binds to the base or abstract
    /// declaration, the body that runs is the override, and without this edge
    /// every override in a solution reports zero tests.
    /// </summary>
    /// <remarks>
    /// Runs in the same pass and for the same reason: the base declaration may
    /// belong to a project loaded later, so both ends must already be nodes.
    /// Carries <c>depth</c> because the chain can be several deep — 1 is the
    /// immediate parent — so a consumer wanting direct replacement only can
    /// filter, exactly as Covers edges carry their own depth.
    /// </remarks>
    internal void AddMethodOverridesEdges(IReadOnlyList<SymbolInfo> symbols)
    {
        foreach (var sym in symbols)
            foreach (var method in sym.MethodNodes)
                for (var i = 0; i < method.OverridesIds.Count; i++)
                {
                    var baseMethodId = method.OverridesIds[i];
                    if (!graph.HasNode(method.Id) || !graph.HasNode(baseMethodId)) continue;

                    var edge = new GraphEdge
                    {
                        FromId = method.Id,
                        ToId = baseMethodId,
                        Type = EdgeType.Overrides
                    };
                    edge.Properties["depth"] = i + 1;
                    graph.AddEdge(edge);
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
        foreach (var dependency in sym.ConstructorDependencies)
        {
            // Framework and Value entries are skipped rather than looked up.
            // The lookup matches on the fullName of nodes this solution
            // declares; a Framework type is declared outside it, and a Value is
            // either out of scope too or an in-solution enum or struct that
            // nothing resolves. Neither could ever have matched, so the skip
            // changes no edge -- measured on two corpora before it went in.
            if (dependency.Kind is DependencyKind.Framework or DependencyKind.Value) continue;

            // A constructed generic is looked up by its open definition only after
            // the name as written misses, so every edge that resolved before still
            // resolves the same way. The solution declares IRepository<T>, never
            // IRepository<CatalogItem>, so without the second lookup the most-used
            // seam in a generic-repository codebase emitted no edge at all.
            //
            // A name with no node gets no edge. There used to be a fallback that
            // stripped a leading "I" and took the first ServiceImplementation
            // anywhere with the remainder as its short name: dead for any
            // namespaced type, since the name looked up is a full name, and on
            // the one shape it could match on (a global-namespace delegate) it
            // joined the parameter to an unrelated class. InjectionLookupTests.
            //
            // Both lookups use the name without nullable reference annotations.
            // Under #nullable enable an IAuditService? parameter is written with
            // the '?', and no node's full name ever has one, so the exact lookup
            // missed and a non-generic type had no second lookup to fall to. The
            // definition name never had the problem: it is rendered from the
            // declaration, which carries no annotation.
            var serviceId = TypeIdFor(dependency.UnannotatedFullName ?? dependency.TypeFullName);
            var viaDefinition = false;
            if (serviceId == null && dependency.DefinitionFullName is { } definition)
            {
                serviceId = TypeIdFor(definition);
                viaDefinition = serviceId != null;
            }
            if (serviceId != null)
            {
                var edge = new GraphEdge
                {
                    FromId = serviceId,
                    ToId = sym.Id,
                    Type = EdgeType.InjectedInto
                };
                // closedType is the parameter type as written, carried only on an
                // edge that reached its node through the definition. Three
                // repositories of different entities are three edges into the same
                // open node, and this is the only thing telling them apart.
                if (viaDefinition) edge.Properties["closedType"] = dependency.TypeFullName;
                graph.AddEdge(edge);
            }
        }
    }
}
