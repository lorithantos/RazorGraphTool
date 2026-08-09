namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorGraph.Core.Graph;

/// <summary>
/// Turns a declared type symbol into the SymbolInfo record its graph nodes are
/// built from: classification by shape (PageModel, controller, service, view
/// model, plain class), plus the property, method, and member detail lists
/// each classification carries.
/// </summary>
internal static class SymbolClassifier
{
    internal static SymbolInfo? ClassifySymbol(
        INamedTypeSymbol symbol, string projectName, Compilation compilation, IReadOnlySet<string> inScope)
    {
        var baseType = symbol.BaseType?.ToDisplayString() ?? "";
        var interfaces = symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList();
        var (filePath, lineStart, lineEnd) = GetLines(symbol);

        // PageModel detection
        if (baseType.Contains("PageModel") || baseType.Contains("Microsoft.AspNetCore.Mvc.RazorPages.PageModel"))
        {
            return new SymbolInfo
            {
                Id = $"pm:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.PageModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Properties = ExtractProperties(symbol),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
                MemberNodes = ExtractMemberNodes(symbol, inScope),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Controller detection
        if (baseType.Contains("Controller") || symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute"))
        {
            return new SymbolInfo
            {
                Id = $"ctrl:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.ApiController,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Methods = ExtractControllerActions(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
                MemberNodes = ExtractMemberNodes(symbol, inScope),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Service detection (heuristic: ends with Service, or implements interface ending with Service)
        if (symbol.Name.EndsWith("Service") || interfaces.Any(i => i.EndsWith("Service")))
        {
            return new SymbolInfo
            {
                Id = $"svc:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = symbol.TypeKind == TypeKind.Interface ? NodeType.ServiceInterface : NodeType.ServiceImplementation,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                ImplementedInterfaces = interfaces.Where(i => i.EndsWith("Service")).ToList(),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
                MemberNodes = ExtractMemberNodes(symbol, inScope)
            };
        }

        // ViewModel detection (heuristic: ends with VM or ViewModel, or used in @model directives)
        if (symbol.Name.EndsWith("VM") || symbol.Name.EndsWith("ViewModel") || symbol.Name.EndsWith("Model"))
        {
            return new SymbolInfo
            {
                Id = $"vm:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.ViewModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                Properties = ExtractProperties(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
                MemberNodes = ExtractMemberNodes(symbol, inScope)
            };
        }

        // Everything else that is still a declared type in this project. Without
        // this the graph silently omits most of the codebase -- helpers, domain
        // types, extension classes -- and "who calls this" cannot be answered
        // because the caller was never a node.
        if (IsCompilerGenerated(symbol)) return null;

        return new SymbolInfo
        {
            Id = $"type:{symbol.ToDisplayString()}",
            Project = projectName,
            Type = NodeType.Class,
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(),
            FilePath = filePath,
            LineStart = lineStart,
            LineEnd = lineEnd,
            BaseType = baseType,
            ImplementedInterfaces = interfaces,
            Properties = ExtractProperties(symbol),
            Methods = ExtractMethods(symbol),
            MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
            MemberNodes = ExtractMemberNodes(symbol, inScope),
            InjectedServices = ExtractInjectedServices(symbol)
        };
    }

    // Names the compiler mints for itself (<>c__DisplayClass, record equality
    // helpers) are never source the user can navigate to.
    private static bool IsCompilerGenerated(INamedTypeSymbol symbol) =>
        symbol.IsImplicitlyDeclared || symbol.Name.StartsWith('<') || symbol.Name.Length == 0;

    /// <summary>
    /// Mapped location: #line directives are honored, so a symbol authored in
    /// a .cshtml reports the .cshtml, not the generated .g.cs the compiler
    /// actually saw. Generated scaffolding (the class declaration itself, the
    /// synthesized ExecuteAsync) sits outside any #line region and keeps its
    /// .g.cs path honestly — the generated marker downstream tells a reader
    /// which kind they are looking at.
    /// </summary>
    private static (string? FilePath, int? Start, int? End) GetLines(INamedTypeSymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return (symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath, null, null);
        var span = syntaxRef.SyntaxTree.GetMappedLineSpan(syntaxRef.Span);
        return (span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private static List<PropertyInfo> ExtractProperties(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IPropertySymbol>()
            .Select(p => new PropertyInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                IsPublic = p.DeclaredAccessibility == Accessibility.Public,
                HasBindProperty = p.GetAttributes().Any(a => a.AttributeClass?.Name == "BindPropertyAttribute")
            })
            .ToList();

    /// <summary>
    /// Every property and field of the type as a candidate graph node,
    /// statics included — a static member is state like any other, and config
    /// caches and singletons live exactly there. Compiler artifacts stay out:
    /// auto-property backing fields and record equality plumbing are not
    /// source a reader can navigate to. Distinct from
    /// <see cref="ExtractProperties"/>, which keeps feeding the class-level
    /// name list existing consumers read.
    /// </summary>
    private static List<MemberDetail> ExtractMemberNodes(INamedTypeSymbol symbol, IReadOnlySet<string> inScope)
    {
        var members = new List<MemberDetail>();
        foreach (var member in symbol.GetMembers())
        {
            if (member.IsImplicitlyDeclared) continue;

            // Razor codegen names its plumbing __tagHelperAttribute_0 and
            // friends — generated source, so IsImplicitlyDeclared is false,
            // but no reader navigates to it. The generated page class's real
            // surface (Model, ViewData) has ordinary names and stays.
            if (member.Name.StartsWith("__", StringComparison.Ordinal)) continue;

            var detail = member switch
            {
                IPropertySymbol p => new MemberDetail
                {
                    Id = SymbolIds.MemberId(p),
                    Name = p.Name,
                    Kind = NodeType.Property,
                    MemberType = p.Type.ToDisplayString(),
                    ReferencedTypeFullNames = InScopeNamedTypes(p.Type, inScope),
                    IsPublic = p.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = p.IsStatic,
                    IsReadOnly = p.IsReadOnly,
                    HasBindProperty = p.GetAttributes().Any(a => a.AttributeClass?.Name == "BindPropertyAttribute")
                },
                // A field fronted by a property (event backing, fixed-size
                // buffers) belongs to its AssociatedSymbol's story, not here.
                IFieldSymbol { AssociatedSymbol: null } f => new MemberDetail
                {
                    Id = SymbolIds.MemberId(f),
                    Name = f.Name,
                    Kind = NodeType.Field,
                    MemberType = f.Type.ToDisplayString(),
                    ReferencedTypeFullNames = InScopeNamedTypes(f.Type, inScope),
                    IsPublic = f.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = f.IsStatic,
                    IsReadOnly = f.IsReadOnly,
                    IsConst = f.IsConst
                },
                _ => null
            };
            if (detail == null) continue;

            var syntaxRef = member.DeclaringSyntaxReferences.FirstOrDefault();
            if (syntaxRef != null)
            {
                var span = syntaxRef.SyntaxTree.GetMappedLineSpan(syntaxRef.Span);
                detail = detail with
                {
                    FilePath = span.Path,
                    LineStart = span.StartLinePosition.Line + 1
                };
            }
            members.Add(detail);
        }
        return members;
    }

    /// <summary>
    /// The in-solution named types a member's declared type mentions: the type
    /// itself, plus type arguments and array elements, recursively — so a
    /// List&lt;Choice&gt; property still references Choice. This is the join that
    /// makes "who uses this type" answerable for DTOs and view models, which
    /// participate in signatures rather than calls.
    /// </summary>
    private static List<string> InScopeNamedTypes(ITypeSymbol type, IReadOnlySet<string> inScope)
    {
        var result = new List<string>();
        Collect(type);
        return result.Distinct().ToList();

        void Collect(ITypeSymbol t)
        {
            switch (t)
            {
                case IArrayTypeSymbol array:
                    Collect(array.ElementType);
                    break;
                case INamedTypeSymbol named:
                    if (named.TypeKind != TypeKind.Error
                        && named.ContainingAssembly?.Name is { } assembly
                        && inScope.Contains(assembly))
                    {
                        result.Add(named.OriginalDefinition.ToDisplayString());
                    }
                    foreach (var arg in named.TypeArguments) Collect(arg);
                    break;
            }
        }
    }

    private static List<MethodInfo> ExtractMethods(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public)
            .Select(m => new MethodInfo
            {
                Name = m.Name,
                ReturnType = m.ReturnType.ToDisplayString(),
                Parameters = m.Parameters.Select(p => p.Type.ToDisplayString()).ToList(),
                IsAsync = m.IsAsync,
                HttpMethod = InferHttpMethod(m)
            })
            .ToList();

    private static List<MethodInfo> ExtractControllerActions(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic)
            .Select(m => new MethodInfo
            {
                Name = m.Name,
                ReturnType = m.ReturnType.ToDisplayString(),
                Parameters = m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList(),
                IsAsync = m.IsAsync,
                HttpMethod = InferHttpMethod(m),
                Route = InferRoute(m)
            })
            .ToList();

    /// <summary>
    /// Every ordinary method and explicit instance constructor on the type, at
    /// any accessibility, as a candidate graph node. Distinct from
    /// <see cref="ExtractMethods"/>, which describes the type's public surface: a
    /// call graph that omitted private methods would break every chain that
    /// passes through a helper.
    ///
    /// Constructors run real code (xUnit's primary setup idiom is the test-class
    /// ctor), so leaving them out made everything reached only through one
    /// invisible to coverage. An implicit default ctor is included only when the
    /// type has instance field/property initializers — then it is exactly the
    /// code that runs them; otherwise it runs nothing and would only ever read
    /// as uncovered noise. Static ctors stay out for the same reason: no
    /// syntactic call site can ever reach one.
    /// </summary>
    private static List<MethodDetail> ExtractMethodNodes(
        INamedTypeSymbol symbol, Compilation compilation, IReadOnlySet<string> inScope)
    {
        var hasInitializers = TypeInitializers.HasInstanceInitializers(symbol);

        var members = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind switch
            {
                MethodKind.Ordinary => !m.IsImplicitlyDeclared,
                MethodKind.Constructor => !m.IsImplicitlyDeclared || hasInitializers,
                _ => false
            })
            .ToList();

        // Lifecycle hooks only count as such on a type that actually has tests;
        // otherwise every IDisposable.Dispose in production code would be flagged.
        var hasTests = members.Any(MethodRoles.IsTestMethod);

        return members
            .Select(m =>
            {
                var syntaxRef = m.DeclaringSyntaxReferences.FirstOrDefault();
                // Mapped: a method authored in a .cshtml block reports the
                // .cshtml; generated scaffolding keeps its .g.cs path.
                FileLinePositionSpan? mapped = syntaxRef == null
                    ? null
                    : syntaxRef.SyntaxTree.GetMappedLineSpan(syntaxRef.Span);
                var declSyntax = syntaxRef?.GetSyntax() as BaseMethodDeclarationSyntax;

                // The semantic model must match the method's own tree — a
                // partial class puts members in trees the type walk never saw.
                var model = declSyntax == null
                    ? null
                    : compilation.GetSemanticModel(declSyntax.SyntaxTree);
                var throws = declSyntax == null || model == null
                    ? new List<ThrownType>()
                    : ExceptionFlow.ExtractThrows(declSyntax, model);
                var (boundaryCatches, boundaryFiltered) =
                    ExceptionFlow.BoundaryCatchSets(m, declSyntax, model);

                return new MethodDetail
                {
                    Id = SymbolIds.MethodId(m),
                    Name = m.Name,
                    Signature = $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString()))})",
                    ReturnType = m.ReturnType.ToDisplayString(),
                    IsAsync = m.IsAsync,
                    IsPublic = m.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = m.IsStatic,
                    IsTest = MethodRoles.IsTestMethod(m),
                    // A test class's ctor is xUnit's primary setup hook — the
                    // framework runs it before every test, no test calls it.
                    IsTestLifecycle = hasTests
                        && (MethodRoles.IsLifecycleMethod(m) || m.MethodKind == MethodKind.Constructor),
                    // Interface members and abstract methods have no body. They are
                    // still nodes worth having (calls bind to them), but they are not
                    // code that a test could execute.
                    IsAbstract = m.IsAbstract,
                    NestingDepth = declSyntax == null ? 0 : BodyGraphExtractor.NestingDepth(declSyntax),
                    Throws = throws,
                    EntryPointKind = MethodRoles.ClassifyEntryPoint(m, inScope),
                    ExtendsTypeFullName = m.IsExtensionMethod
                        ? m.Parameters[0].Type.OriginalDefinition.ToDisplayString()
                        : null,
                    ImplementsIds = InSolutionImplementedMembers(m, inScope),
                    BoundaryCatches = boundaryCatches,
                    BoundaryCatchesFiltered = boundaryFiltered,
                    FilePath = mapped?.Path ?? m.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                    LineStart = mapped?.StartLinePosition.Line + 1
                };
            })
            .ToList();
    }

    /// <summary>
    /// Ids of in-solution interface methods this method implements — the join
    /// that lets escape propagation cross DI: callers bind to the interface,
    /// the throw lives in the implementation, and without this edge the chain
    /// dies at the boundary that is ASP.NET's default architecture.
    /// </summary>
    private static List<string> InSolutionImplementedMembers(IMethodSymbol m, IReadOnlySet<string> inScope)
    {
        if (m.IsStatic || m.MethodKind != MethodKind.Ordinary) return new List<string>();

        var ids = new List<string>();
        foreach (var iface in m.ContainingType.AllInterfaces)
        {
            if (iface.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) continue;

            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        m.ContainingType.FindImplementationForInterfaceMember(member), m))
                    ids.Add(SymbolIds.MethodId(member));
            }
        }
        return ids;
    }

    private static List<string> ExtractInjectedServices(INamedTypeSymbol symbol)
    {
        var ctor = symbol.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public);
        if (ctor == null) return new List<string>();
        return ctor.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
    }

    private static string? InferHttpMethod(IMethodSymbol method)
    {
        var attrs = method.GetAttributes();
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpGetAttribute")) return "GET";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPostAttribute")) return "POST";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPutAttribute")) return "PUT";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpDeleteAttribute")) return "DELETE";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPatchAttribute")) return "PATCH";
        return null;
    }

    private static string? InferRoute(IMethodSymbol method)
    {
        var routeAttr = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "HttpGetAttribute" or "HttpPostAttribute");
        if (routeAttr == null) return null;
        return routeAttr.ConstructorArguments.FirstOrDefault().Value?.ToString();
    }
}
