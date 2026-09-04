namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor.Attributes;

/// <summary>
/// Turns a declared type symbol into the SymbolInfo record its graph nodes are
/// built from: classification by shape (PageModel, controller, service, view
/// model, plain class), plus the property, method, and member detail lists
/// each classification carries.
/// </summary>
internal static class SymbolClassifier
{
    internal static SymbolInfo? ClassifySymbol(
        INamedTypeSymbol symbol, string projectName, Compilation compilation, IReadOnlySet<string> inScope,
        AttributePolicy policy)
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
                Attributes = ExtractAttributes(symbol, "type", inScope),
                Type = NodeType.PageModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Properties = ExtractProperties(symbol, policy),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
                MemberNodes = ExtractMemberNodes(symbol, inScope, policy),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Controller detection — the shared predicate, so this branch and the
        // entry-point classifier can never disagree.
        if (MethodRoles.IsControllerType(symbol, policy))
        {
            return new SymbolInfo
            {
                Id = $"ctrl:{symbol.ToDisplayString()}",
                Project = projectName,
                Attributes = ExtractAttributes(symbol, "type", inScope),
                Type = NodeType.ApiController,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Methods = ExtractControllerActions(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
                MemberNodes = ExtractMemberNodes(symbol, inScope, policy),
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
                Attributes = ExtractAttributes(symbol, "type", inScope),
                Type = symbol.TypeKind == TypeKind.Interface ? NodeType.ServiceInterface : NodeType.ServiceImplementation,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                ImplementedInterfaces = interfaces.Where(i => i.EndsWith("Service")).ToList(),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
                MemberNodes = ExtractMemberNodes(symbol, inScope, policy)
            };
        }

        // ViewModel detection (heuristic: ends with VM or ViewModel, or used in @model directives)
        if (symbol.Name.EndsWith("VM") || symbol.Name.EndsWith("ViewModel") || symbol.Name.EndsWith("Model"))
        {
            return new SymbolInfo
            {
                Id = $"vm:{symbol.ToDisplayString()}",
                Project = projectName,
                Attributes = ExtractAttributes(symbol, "type", inScope),
                Type = NodeType.ViewModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                Properties = ExtractProperties(symbol, policy),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
                MemberNodes = ExtractMemberNodes(symbol, inScope, policy)
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
            Attributes = ExtractAttributes(symbol, "type", inScope),
            Type = NodeType.Class,
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(),
            FilePath = filePath,
            LineStart = lineStart,
            LineEnd = lineEnd,
            BaseType = baseType,
            ImplementedInterfaces = interfaces,
            Properties = ExtractProperties(symbol, policy),
            Methods = ExtractMethods(symbol),
            MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
            MemberNodes = ExtractMemberNodes(symbol, inScope, policy),
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

    private static List<PropertyInfo> ExtractProperties(INamedTypeSymbol symbol, AttributePolicy policy) =>
        symbol.GetMembers().OfType<IPropertySymbol>()
            .Select(p => new PropertyInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                IsPublic = p.DeclaredAccessibility == Accessibility.Public,
                HasBindProperty = HasBindProperty(p, policy)
            })
            .ToList();

    private static bool HasBindProperty(IPropertySymbol property, AttributePolicy policy) =>
        property.GetAttributes().Any(a =>
            a.AttributeClass != null && policy.BindPropertyAttributeNames.Contains(a.AttributeClass.Name));

    /// <summary>
    /// Every property and field of the type as a candidate graph node,
    /// statics included — a static member is state like any other, and config
    /// caches and singletons live exactly there. Compiler artifacts stay out:
    /// auto-property backing fields and record equality plumbing are not
    /// source a reader can navigate to. Distinct from
    /// <see cref="ExtractProperties"/>, which keeps feeding the class-level
    /// name list existing consumers read.
    /// </summary>
    private static List<MemberDetail> ExtractMemberNodes(
        INamedTypeSymbol symbol, IReadOnlySet<string> inScope, AttributePolicy policy)
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
                    HasBindProperty = HasBindProperty(p, policy),
                    Attributes = ExtractAttributes(p, "property", inScope)
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
                    IsConst = f.IsConst,
                    Attributes = ExtractAttributes(f, "field", inScope)
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
        INamedTypeSymbol symbol, Compilation compilation, IReadOnlySet<string> inScope, AttributePolicy policy)
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
        var hasTests = members.Any(m => MethodRoles.IsTestMethod(m, policy));

        return members
            .Select(m =>
            {
                var syntaxRef = m.DeclaringSyntaxReferences.FirstOrDefault();
                // Mapped: a method authored in a .cshtml block reports the
                // .cshtml; generated scaffolding keeps its .g.cs path.
                FileLinePositionSpan? mapped = syntaxRef == null
                    ? null
                    : syntaxRef.SyntaxTree.GetMappedLineSpan(syntaxRef.Span);
                // The scope holding this method's body: an ordinary declaration,
                // or the compilation unit when the method is the synthesized
                // entry point of a top-level program. Deliberately not "whatever
                // syntax the symbol points at" — a primary constructor's
                // reference is its *type* declaration, and walking that would
                // attribute every throw in the type to the constructor.
                var bodyScope = syntaxRef?.GetSyntax() switch
                {
                    BaseMethodDeclarationSyntax d => (SyntaxNode)d,
                    CompilationUnitSyntax unit => unit,
                    _ => null
                };
                var declSyntax = bodyScope as BaseMethodDeclarationSyntax;

                // The semantic model must match the method's own tree — a
                // partial class puts members in trees the type walk never saw.
                var model = bodyScope == null
                    ? null
                    : compilation.GetSemanticModel(bodyScope.SyntaxTree);
                var throws = bodyScope == null || model == null
                    ? new List<ThrownType>()
                    : ExceptionFlow.ExtractThrows(bodyScope, model);
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
                    IsTest = MethodRoles.IsTestMethod(m, policy),
                    // A test class's ctor is xUnit's primary setup hook — the
                    // framework runs it before every test, no test calls it.
                    IsTestLifecycle = hasTests
                        && (MethodRoles.IsLifecycleMethod(m, policy) || m.MethodKind == MethodKind.Constructor),
                    // Interface members and abstract methods have no body. They are
                    // still nodes worth having (calls bind to them), but they are not
                    // code that a test could execute.
                    IsAbstract = m.IsAbstract,
                    NestingDepth = declSyntax == null ? 0 : BodyGraphExtractor.NestingDepth(declSyntax),
                    Throws = throws,
                    EntryPointKind = MethodRoles.ClassifyEntryPoint(m, inScope, policy),
                    ExtendsTypeFullName = m.IsExtensionMethod
                        ? m.Parameters[0].Type.OriginalDefinition.ToDisplayString()
                        : null,
                    ImplementsIds = InSolutionImplementedMembers(m, inScope),
                    BoundaryCatches = boundaryCatches,
                    BoundaryCatchesFiltered = boundaryFiltered,
                    FilePath = mapped?.Path ?? m.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                    LineStart = mapped?.StartLinePosition.Line + 1,
                    Attributes = ExtractMethodAttributes(m, inScope),
                    Parameters = ExtractParameterNodes(m, inScope)
                };
            })
            .ToList();
    }

    /// <summary>
    /// The method's DECORATED parameters — the only ones that become nodes; see
    /// ParameterDetail. Enumerated from the unreduced original definition so
    /// names and ordinals match the form MethodId is built from: a reduced
    /// extension method has folded its this parameter away, and reading the
    /// reduced list would shift every ordinal by one.
    /// </summary>
    private static List<ParameterDetail> ExtractParameterNodes(IMethodSymbol method, IReadOnlySet<string> inScope)
    {
        var def = (method.ReducedFrom ?? method).OriginalDefinition;

        var result = new List<ParameterDetail>();
        foreach (var p in def.Parameters)
        {
            if (p.GetAttributes().Length == 0) continue;

            result.Add(new ParameterDetail(
                SymbolIds.ParameterId(method, p),
                p.Name,
                p.Ordinal,
                p.Type.ToDisplayString(),
                p.Locations.FirstOrDefault()?.GetMappedLineSpan().StartLinePosition.Line + 1)
            {
                Attributes = ExtractAttributes(p, "parameter", inScope)
            });
        }
        return result;
    }

    /// <summary>
    /// Attributes written at one site, recorded as usages rather than reduced to
    /// a yes/no.
    /// </summary>
    /// <remarks>
    /// Four predicates in this extractor already read attributes — test,
    /// lifecycle, controller, bind-property — and every one of them consumes the
    /// fact and discards it. [Fact] decides a node is a test method, and then
    /// that [Fact] was ever there is unrecoverable. This keeps it.
    ///
    /// One entry per SITE, not per attribute type: [Theory] with twenty
    /// [InlineData] is twenty-one entries, and collapsing them would lose the
    /// lines that tell them apart.
    /// </remarks>
    private static List<AttributeUsage> ExtractAttributes(ISymbol symbol, string target, IReadOnlySet<string> inScope) =>
        symbol.GetAttributes().Select(a => Describe(a, target, inScope)).ToList();

    /// <summary>
    /// Attributes written on the assembly and module themselves — the ones that
    /// hang off the Project node, since assembly and project are the same grain
    /// here. Generated sites are excluded: the SDK writes ~10 AssemblyInfo
    /// attributes per project into obj\, and emitting those would bury the
    /// hand-written manifest attributes (OrchardCore's [assembly: Module] and
    /// [assembly: Feature] are the measured case) under uniform build noise.
    /// </summary>
    internal static List<AttributeUsage> ExtractAssemblyAttributes(Compilation compilation, IReadOnlySet<string> inScope)
    {
        var usages = compilation.Assembly.GetAttributes()
            .Where(a => !GeneratedCodeMap.IsGeneratedSite(a))
            .Select(a => Describe(a, "assembly", inScope))
            .ToList();
        usages.AddRange(compilation.SourceModule.GetAttributes()
            .Where(a => !GeneratedCodeMap.IsGeneratedSite(a))
            .Select(a => Describe(a, "module", inScope)));
        return usages;
    }

    /// <summary>
    /// A method's own attributes plus its return value's.
    /// </summary>
    /// <remarks>
    /// Return-value attributes live on a separate Roslyn collection, so asking
    /// only for GetAttributes drops [return: MarshalAs] silently — twelve of them
    /// in DriveSurvey's interop layer, which is precisely where a reader needs to
    /// see the marshalling declared.
    /// </remarks>
    private static List<AttributeUsage> ExtractMethodAttributes(IMethodSymbol method, IReadOnlySet<string> inScope)
    {
        var attributes = ExtractAttributes(method, "method", inScope);
        attributes.AddRange(method.GetReturnTypeAttributes().Select(a => Describe(a, "return", inScope)));
        return attributes;
    }

    /// <summary>
    /// One attribute usage, resolved through the attribute class's original
    /// definition so RegisterDependency&lt;IFoo&gt; and RegisterDependency&lt;IBar&gt;
    /// name one type rather than two — the trap SymbolIds.MethodId already
    /// documents for generic methods.
    /// </summary>
    /// <remarks>
    /// An attribute whose class did not bind is kept, with the reason, rather
    /// than skipped. C# resolves attribute types at compile time, so this cannot
    /// happen while the compilation is clean; its presence is evidence the build
    /// had errors. Dropping it would turn a broken compile into a quietly smaller
    /// graph, which is the failure mode that costs the most to notice.
    /// </remarks>
    private static AttributeUsage Describe(AttributeData data, string target, IReadOnlySet<string> inScope)
    {
        var line = data.ApplicationSyntaxReference is { } reference
            ? reference.GetSyntax().GetLocation().GetLineSpan().StartLinePosition.Line + 1
            : (int?)null;

        if (data.AttributeClass is not { } attributeClass || attributeClass.TypeKind == TypeKind.Error)
        {
            var written = data.AttributeClass?.ToDisplayString() ?? "<unresolved>";
            return new AttributeUsage(written, data.AttributeClass?.Name ?? "<unresolved>",
                Assembly: null, Target: target, Line: line,
                UnresolvedReason: "attribute class did not bind — the compilation has errors");
        }

        var definition = attributeClass.OriginalDefinition;
        var (args, named, unresolvedArgs) = ExtractArguments(data);

        // The generic instantiation's type arguments, from the class as USED —
        // the definition above deliberately erased them so the node stays one
        // per attribute type.
        List<string>? typeArgs = attributeClass.TypeArguments.Length > 0
            ? attributeClass.TypeArguments.Select(t => t.ToDisplayString()).ToList()
            : null;

        // As written, without the enclosing parens. From syntax rather than
        // reconstructed, so what a reader sees is what the author typed.
        string? source = null;
        if (data.ApplicationSyntaxReference?.GetSyntax() is AttributeSyntax { ArgumentList.Arguments.Count: > 0 } syntax)
            source = syntax.ArgumentList!.Arguments.ToString();

        return new AttributeUsage(
            definition.ToDisplayString(),
            definition.Name,
            definition.ContainingAssembly?.Name,
            target,
            line)
        {
            Args = args,
            Named = named,
            TypeArgs = typeArgs,
            RegisteredTypeFullNames = RegisteredTypes(data, attributeClass, inScope),
            Source = source,
            UnresolvedArgs = unresolvedArgs
        };
    }

    /// <summary>
    /// In-scope named types the usage names through typeof(...) arguments —
    /// positional, named, or nested in arrays — and through the generic
    /// instantiation's type arguments. These are the types a framework will
    /// construct or consult because of the annotation, with no call site
    /// anywhere; the Registers edge exists to make them navigable.
    /// </summary>
    private static List<string>? RegisteredTypes(
        AttributeData data, INamedTypeSymbol attributeClass, IReadOnlySet<string> inScope)
    {
        var result = new List<string>();

        foreach (var typeArg in attributeClass.TypeArguments)
            result.AddRange(InScopeNamedTypes(typeArg, inScope));

        foreach (var constant in data.ConstructorArguments) Collect(constant);
        foreach (var (_, constant) in data.NamedArguments) Collect(constant);

        return result.Count > 0 ? result.Distinct().ToList() : null;

        void Collect(TypedConstant constant)
        {
            switch (constant.Kind)
            {
                case TypedConstantKind.Type when constant.Value is ITypeSymbol type:
                    result.AddRange(InScopeNamedTypes(type, inScope));
                    break;
                case TypedConstantKind.Array when !constant.IsNull:
                    foreach (var element in constant.Values) Collect(element);
                    break;
            }
        }
    }

    /// <summary>
    /// Argument values from the SEMANTIC layer, not the syntax. TypedConstant
    /// hands over what the compiler already resolved: new[]{...} and the C# 12
    /// [...] form are one Array case, an enum member arrives as its value with
    /// its type, and there is no such thing as an unevaluable constant in
    /// compiling C# — the only Error case is a compilation that did not build,
    /// which the caller reports as exactly that.
    /// </summary>
    private static (List<object?>? Args, Dictionary<string, object?>? Named, List<string>? Unresolved)
        ExtractArguments(AttributeData data)
    {
        List<string>? unresolved = null;

        List<object?>? args = null;
        if (data.ConstructorArguments.Length > 0)
        {
            args = new List<object?>(data.ConstructorArguments.Length);
            for (var i = 0; i < data.ConstructorArguments.Length; i++)
            {
                var constant = data.ConstructorArguments[i];
                if (constant.Kind == TypedConstantKind.Error)
                {
                    (unresolved ??= new()).Add(i.ToString());
                    args.Add(null); // the slot survives so later indices keep their positions
                    continue;
                }
                args.Add(Render(constant));
            }
        }

        Dictionary<string, object?>? named = null;
        if (data.NamedArguments.Length > 0)
        {
            named = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, constant) in data.NamedArguments)
            {
                if (constant.Kind == TypedConstantKind.Error)
                {
                    (unresolved ??= new()).Add(name);
                    named[name] = null;
                    continue;
                }
                named[name] = Render(constant);
            }
        }

        return (args, named, unresolved);
    }

    /// <summary>
    /// One constant, rendered into the value set the serializer round-trips
    /// identically. An Error nested INSIDE an array becomes a bare null slot —
    /// the index path is not reported, only top-level failures are, which is a
    /// known simplification rather than an oversight.
    /// </summary>
    private static object? Render(TypedConstant constant) => constant.Kind switch
    {
        // A null passed where an array is expected still has Kind Array, with
        // Values unset — reading either Values or Value would throw, so the
        // null has to be answered before touching them.
        TypedConstantKind.Array => constant.IsNull
            ? null
            : constant.Values
                .Select(v => v.Kind == TypedConstantKind.Error ? null : Render(v))
                .ToList(),
        // typeof(X) keeps its typeof spelling so a reader cannot mistake it for
        // the string "X". The navigable form (a Registers edge) comes separately.
        TypedConstantKind.Type => $"typeof({(constant.Value as ITypeSymbol)?.ToDisplayString() ?? "?"})",
        TypedConstantKind.Enum => RenderEnum(constant),
        _ => Scalar(constant.Value)
    };

    /// <summary>
    /// An enum constant as the member the author named, when one field carries
    /// exactly that value; the raw number otherwise (flags combinations, or a
    /// value outside the declared members). The member name is what was written
    /// and what a reader can grep for; the number is neither.
    /// </summary>
    private static object? RenderEnum(TypedConstant constant)
    {
        if (constant.Type is INamedTypeSymbol enumType && constant.Value is { } value)
        {
            var member = enumType.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value));
            if (member != null) return $"{enumType.Name}.{member.Name}";
        }
        return Scalar(constant.Value);
    }

    /// <summary>
    /// Narrow a constant to the shapes GraphSerializer.NormalizeValue rebuilds
    /// on load — string, bool, int, long, double — so a value means the same
    /// thing after a save as before one. char becomes a one-character string
    /// and the small integers widen to int for the same reason: what comes back
    /// from JSON is what went in.
    /// </summary>
    private static object? Scalar(object? value) => value switch
    {
        null => null,
        // NaN and the infinities are doubles with no JSON number to be written
        // as, so they travel as their names. They have to be caught before the
        // double and float arms below, which would pass them through to a writer
        // that throws on them -- one [InlineData(double.NaN)] in a guard test is
        // enough to fail the save of an entire solution graph. Naming them beats
        // the alternative: AllowNamedFloatingPointLiterals emits bare NaN tokens
        // that are not valid JSON and that no other reader of these graphs takes.
        double d when !double.IsFinite(d) => NonFinite(d),
        float nf when !float.IsFinite(nf) => NonFinite(nf),
        string or bool or int or long or double => value,
        char c => c.ToString(),
        sbyte or byte or short or ushort => Convert.ToInt32(value),
        uint u => (long)u,
        float f => (double)f,
        decimal m => (double)m,
        ulong ul => (double)ul,
        _ => value.ToString()
    };

    /// <summary>
    /// The name C# writes for a double that has no numeric form. Matches
    /// <c>ToString(CultureInfo.InvariantCulture)</c>, so what a reader greps for
    /// is what the source said.
    /// </summary>
    private static string NonFinite(double value) =>
        double.IsNaN(value) ? "NaN" : value > 0 ? "Infinity" : "-Infinity";

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
