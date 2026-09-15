namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
        IReadOnlySet<string> registeredServices, AttributePolicy policy)
    {
        // A C# 14 extension block is a TypeDeclarationSyntax, so the type walk
        // reaches it — but it is syntax, not a type a reader navigates to. It
        // has no name, and its members belong to the static class holding it,
        // which is where ExtractMethodNodes and ExtractMemberNodes collect them
        // from. Skipping it here is deliberate rather than incidental:
        // IsCompilerGenerated below would also drop it, on the empty name, and
        // filing user-written syntax under "compiler generated" is exactly how
        // every member inside one stayed invisible.
        if (symbol.IsExtension) return null;

        var baseType = symbol.BaseType?.ToDisplayString() ?? "";
        var interfaces = symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList();

        // Flat for the naming heuristics above, with distances for the graph.
        // AllInterfaces cannot say which of these the declaration itself names,
        // and that is the half a reader asks for first.
        var implemented = InterfaceImplementations(symbol);
        var (filePath, lineStart, lineEnd, docsStart) = GetLines(symbol);

        // The one place a type node is built. The classification below decides
        // only three things -- the id prefix, the kind, and (for a controller)
        // which extractor describes the method surface -- and everything a node
        // carries is set here, once, for all of them.
        //
        // This is structural rather than tidy. Five parallel object initialisers
        // produced three separate defects of the same shape, each a field wired
        // into some branches and not others: ImplementedInterfaces on two of
        // five, baseType on three of five, isStatic caught only by accident. The
        // divergence was invisible to the obvious test because the fixture you
        // reach for while editing a branch is a type that routes down it. With
        // one initialiser a new field cannot be added to a subset -- there is no
        // subset to add it to. SymbolClassifierAgreementTests holds the line if
        // this ever splits again, and found three more gaps of the same shape on
        // its first run: Properties absent from two branches, Methods from one,
        // InjectedServices from two.
        //
        // That last was the expensive one, and not for the reason first given.
        // AddInjectionEdges reads InjectedServices and nothing else, so a type on
        // the service or view-model branch emitted no injection edge at all.
        // Measured across the rotation on 2026-09-15, restoring it added 61 to
        // RetirementCore's InjectedInto count and nothing to GameHub's, which has
        // no type on either branch -- and all 61 came from view models. Its one
        // service node contributed none. The service branch was the guess; the
        // view-model branch was the finding.
        SymbolInfo Node(string idPrefix, NodeType type, List<MethodInfo>? methods = null) =>
            new()
            {
                Id = $"{idPrefix}:{symbol.ToDisplayString()}",
                Project = projectName,
                Attributes = ExtractAttributes(symbol, "type", inScope),
                Type = type,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                IsPublic = IsEffectivelyPublic(symbol),
                IsInterface = symbol.TypeKind == TypeKind.Interface,
                IsStatic = symbol.IsStatic,
                FilePath = filePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                DocsStart = docsStart,
                BaseType = baseType,
                ImplementedInterfaces = implemented,
                Properties = ExtractProperties(symbol, policy),
                Methods = methods ?? ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope, policy),
                MemberNodes = ExtractMemberNodes(symbol, inScope, policy),
                InjectedServices = ExtractInjectedServices(symbol)
            };

        // PageModel detection
        if (baseType.Contains("PageModel") || baseType.Contains("Microsoft.AspNetCore.Mvc.RazorPages.PageModel"))
            return Node("pm", NodeType.PageModel);

        // Controller detection — the shared predicate, so this branch and the
        // entry-point classifier can never disagree. Its action list is the one
        // genuine per-branch difference left: a controller's method surface is
        // its routable actions, not its public methods.
        if (MethodRoles.IsControllerType(symbol, policy))
            return Node("ctrl", NodeType.ApiController, ExtractControllerActions(symbol));

        // Service detection: registration OR name. A type is a service when an
        // attribute registration names it -- as the service type, or as the
        // implementation carrying the attribute (see RegisteredServiceTypes) --
        // or when its own name, or an interface it implements, ends in
        // "Service".
        //
        // The name fallback is retained deliberately, and this rule must only
        // ever ADD promotions. A codebase that registers at call sites
        // (services.AddScoped<IFoo, Foo>()) rather than by attribute -- which
        // is how RetirementCore and this repository itself work -- shows the
        // extractor no registration at all, and dropping the fallback would
        // demote every service in it. The fallback is a convention guess
        // standing in for a registration the extractor cannot observe.
        if (registeredServices.Contains(symbol.ToDisplayString())
            || symbol.Name.EndsWith("Service") || interfaces.Any(i => i.EndsWith("Service")))
            return Node("svc", symbol.TypeKind == TypeKind.Interface
                ? NodeType.ServiceInterface
                : NodeType.ServiceImplementation);

        // ViewModel detection (heuristic: ends with VM or ViewModel, or used in @model directives)
        if (symbol.Name.EndsWith("VM") || symbol.Name.EndsWith("ViewModel") || symbol.Name.EndsWith("Model"))
            return Node("vm", NodeType.ViewModel);

        // Everything else that is still a declared type in this project. Without
        // this the graph silently omits most of the codebase -- helpers, domain
        // types, extension classes -- and "who calls this" cannot be answered
        // because the caller was never a node.
        if (IsCompilerGenerated(symbol)) return null;

        return Node("type", NodeType.Class);
    }

    /// <summary>
    /// Reachable from outside the assembly: public, and nested only inside
    /// public types. The containment walk is the whole point — a public type
    /// nested in an internal one is not reachable by anyone, and calling it
    /// public would put false findings in every visibility report.
    /// </summary>
    private static bool IsEffectivelyPublic(INamedTypeSymbol symbol)
    {
        for (INamedTypeSymbol? t = symbol; t != null; t = t.ContainingType)
            if (t.DeclaredAccessibility != Accessibility.Public) return false;
        return true;
    }

    /// <summary>
    /// In-solution types named by a method's signature: return type and every
    /// parameter, recursively through generic arguments. See
    /// MethodDetail.SignatureTypeFullNames for why these are not optional.
    /// </summary>
    private static List<string> SignatureTypes(IMethodSymbol m, IReadOnlySet<string> inScope)
    {
        var names = InScopeNamedTypes(m.ReturnType, inScope);
        foreach (var parameter in m.Parameters)
            foreach (var name in InScopeNamedTypes(parameter.Type, inScope))
                if (!names.Contains(name))
                    names.Add(name);
        return names;
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
    private static (string? FilePath, int? Start, int? End, int? DocsStart) GetLines(INamedTypeSymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return (symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath, null, null, null);
        var span = DeclarationSpan(syntaxRef);
        return (span.Path, span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1, DocumentationLine(syntaxRef));
    }

    /// <summary>
    /// First line of the declaration's XML documentation comment, or null when
    /// it has none.
    /// </summary>
    /// <remarks>
    /// This is what lets a reader take exactly the chunk it meant. A node used
    /// to say "line 91" and nothing else, so opening it meant guessing how far
    /// down the member ran and how far up its documentation started; with this
    /// and LineEnd, <c>docsStart ?? lineStart</c> through <c>lineEnd</c> is the
    /// member and its documentation, exactly.
    /// <para>
    /// Read from the whole declaration's leading trivia, which sits above the
    /// attribute list — attributes are part of the declaration node, the doc
    /// comment is trivia in front of it. Ordinary <c>//</c> comments are
    /// deliberately not counted: a remark that happens to sit above a method is
    /// not that method's documentation, and treating it as such would drag in
    /// whatever unrelated text preceded it.
    /// </para>
    /// </remarks>
    private static int? DocumentationLine(SyntaxReference syntaxRef)
    {
        var trivia = syntaxRef.GetSyntax().GetLeadingTrivia();
        foreach (var t in trivia)
        {
            if (t.RawKind is (int)SyntaxKind.SingleLineDocumentationCommentTrivia
                          or (int)SyntaxKind.MultiLineDocumentationCommentTrivia)
            {
                return syntaxRef.SyntaxTree.GetMappedLineSpan(t.FullSpan).StartLinePosition.Line + 1;
            }
        }
        return null;
    }

    /// <summary>
    /// Where the declaration itself begins, mapped through any #line directive.
    ///
    /// Attributes are part of the declaration node rather than leading trivia,
    /// so a syntax reference's own span starts at the '[' — and every attributed
    /// declaration reported the attribute's line as its own. A caller who opens
    /// the node lands a line or more above what they asked for, and on this repo
    /// that was every [McpServerToolType] class and every [Fact] test method.
    /// The end is left alone: it is the closing brace either way.
    /// </summary>
    private static FileLinePositionSpan DeclarationSpan(SyntaxReference syntaxRef)
    {
        var span = syntaxRef.Span;
        if (syntaxRef.GetSyntax() is MemberDeclarationSyntax { AttributeLists.Count: > 0 } member)
        {
            // The first child that is not an attribute list: a modifier, or the
            // return type / keyword when the declaration carries no modifier.
            var declaration = member.ChildNodesAndTokens()
                .FirstOrDefault(child => child.AsNode() is not AttributeListSyntax);
            if (declaration != default)
                span = Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(declaration.SpanStart, span.End);
        }
        return syntaxRef.SyntaxTree.GetMappedLineSpan(span);
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
        // Extension-block properties are members of the type holding the block:
        // the block itself is never a node, so nothing else would carry them.
        foreach (var member in symbol.GetMembers().Concat(ExtensionBlockMembers<ISymbol>(symbol)))
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
                    ImplementsIds = InSolutionImplementedProperties(p, inScope),
                    IsPublic = p.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = p.IsStatic,
                    IsReadOnly = p.IsReadOnly,
                    IsOverride = p.IsOverride,
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
                var span = DeclarationSpan(syntaxRef);
                detail = detail with
                {
                    FilePath = span.Path,
                    LineStart = span.StartLinePosition.Line + 1,
                    LineEnd = span.EndLinePosition.Line + 1,
                    DocsStart = DocumentationLine(syntaxRef)
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

    /// <summary>
    /// Every member an extension block inside this type declares, as the
    /// members they are rather than as the static methods they lower to.
    /// </summary>
    /// <remarks>
    /// The containing static class holds both: the block, as a nameless nested
    /// type, and one implicitly-declared static implementation per member. The
    /// implementations are what the compiler emits and what a caller never
    /// names, so every list here reads the block instead — real source
    /// locations, real accessibility, real accessors — and the existing
    /// <c>!IsImplicitlyDeclared</c> filters keep the implementations out, which
    /// is what stops each member arriving twice.
    /// </remarks>
    private static IEnumerable<T> ExtensionBlockMembers<T>(INamedTypeSymbol symbol) where T : ISymbol =>
        symbol.GetTypeMembers()
            .Where(t => t.IsExtension)
            .SelectMany(t => t.GetMembers())
            .Where(m => !m.IsImplicitlyDeclared)
            .OfType<T>();

    /// <summary>
    /// The receiver an extension member extends, whichever syntax declared it:
    /// the first parameter of a classic <c>this</c> method, or the enclosing
    /// block's receiver. A static extension block still extends its type, so it
    /// answers here too.
    /// </summary>
    private static string? ExtendedTypeOf(IMethodSymbol method)
    {
        if (method.IsExtensionMethod)
            return method.Parameters[0].Type.OriginalDefinition.ToDisplayString();

        if (method.ContainingType is { IsExtension: true, ExtensionParameter: { } receiver })
            return receiver.Type.OriginalDefinition.ToDisplayString();

        return null;
    }

    private static List<MethodInfo> ExtractMethods(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>()
            // Implicitly declared: the lowered implementations of extension
            // members, which would otherwise advertise accessor names like
            // get_IsFree on the containing type. The block's own members are
            // appended below, under the names a caller actually writes.
            .Where(m => !m.IsImplicitlyDeclared)
            .Concat(ExtensionBlockMembers<IMethodSymbol>(symbol))
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
        // The copy-constructor shape: exactly one parameter typed as the
        // containing type itself. Shape rather than IsRecord, because the shape
        // is what makes it a copy constructor.
        static bool IsCopyConstructorShape(IMethodSymbol ctor) =>
            ctor.Parameters.Length == 1
            && SymbolEqualityComparer.Default.Equals(ctor.Parameters[0].Type, ctor.ContainingType);

        var hasInitializers = TypeInitializers.HasInstanceInitializers(symbol);

        var members = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind switch
            {
                MethodKind.Ordinary => !m.IsImplicitlyDeclared,
                // A record's synthesized copy ctor is implicitly declared too,
                // but it copies fields rather than running initializers (those
                // run in the primary or default ctor), and it has no syntax to
                // point a row at — so initializers do not admit it.
                MethodKind.Constructor => !m.IsImplicitlyDeclared
                    || (hasInitializers && !IsCopyConstructorShape(m)),
                _ => false
            })
            // Extension-block methods are nodes of the type that holds the
            // block. Accessors stay out here as everywhere else — an extension
            // property's body attributes to its Property node.
            .Concat(ExtensionBlockMembers<IMethodSymbol>(symbol)
                .Where(m => m.MethodKind == MethodKind.Ordinary))
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
                    : DeclarationSpan(syntaxRef);
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
                    IsOverride = m.IsOverride,
                    IsPrimaryConstructor = m.MethodKind == MethodKind.Constructor
                        && m.DeclaringSyntaxReferences.Any(r => r.GetSyntax() is Microsoft.CodeAnalysis.CSharp.Syntax.TypeDeclarationSyntax),
                    NestingDepth = declSyntax == null ? 0 : BodyGraphExtractor.NestingDepth(declSyntax),
                    Throws = throws,
                    EntryPointKind = MethodRoles.ClassifyEntryPoint(m, inScope, policy),
                    SignatureTypeFullNames = SignatureTypes(m, inScope),
                    ExtendsTypeFullName = ExtendedTypeOf(m),
                    ImplementsIds = InSolutionImplementedMembers(m, inScope),
                    OverridesIds = InSolutionOverriddenMembers(m, inScope),
                    BoundaryCatches = boundaryCatches,
                    BoundaryCatchesFiltered = boundaryFiltered,
                    FilePath = mapped?.Path ?? m.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                    LineStart = mapped?.StartLinePosition.Line + 1,
                    LineEnd = mapped?.EndLinePosition.Line + 1,
                    DocsStart = syntaxRef == null ? null : DocumentationLine(syntaxRef),
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
        // Canonical is the same resolution ParameterId uses, so the parameters
        // enumerated here and the ids minted for them can never disagree —
        // including for an extension-block member, whose implementation carries
        // a receiver the declaration itself does not.
        var def = SymbolIds.Canonical(method);

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
    /// Every in-scope type an attribute registration names, across the whole
    /// compilation, plus the type carrying each such attribute: for
    /// <c>[RegisterDependency&lt;ILori&gt;] class LoriImpl2 : ILori</c> that is
    /// both <c>ILori</c> (the service type) and <c>LoriImpl2</c> (the registered
    /// implementation). Gathered BEFORE the type walk because classification is
    /// per-symbol and the attribute sits on the implementation, so when
    /// ClassifySymbol reaches the interface there is nothing on that symbol to
    /// read.
    /// </summary>
    /// <remarks>
    /// Walks symbols, not syntax: no syntax tree or semantic model is touched,
    /// which keeps it cheap and independent of the tree walk it runs ahead of.
    /// Reuses RegisteredTypes so the set agrees exactly with the Registers edges
    /// the attribute extraction emits.
    /// </remarks>
    internal static IReadOnlySet<string> RegisteredServiceTypes(
        Compilation compilation, IReadOnlySet<string> inScope)
    {
        var result = new HashSet<string>();
        Visit(compilation.GlobalNamespace);
        return result;

        void Visit(INamespaceSymbol ns)
        {
            foreach (var child in ns.GetNamespaceMembers()) Visit(child);
            foreach (var type in ns.GetTypeMembers()) VisitType(type);
        }

        void VisitType(INamedTypeSymbol type)
        {
            foreach (var attr in type.GetAttributes())
            {
                if (attr.AttributeClass is not { } attributeClass || attributeClass.TypeKind == TypeKind.Error)
                    continue;
                if (RegisteredTypes(attr, attributeClass, inScope) is not { } registered) continue;

                result.UnionWith(registered);
                result.Add(type.ToDisplayString());
            }

            foreach (var nested in type.GetTypeMembers()) VisitType(nested);
        }
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

    /// <summary>
    /// Ids of the in-solution interface properties this property implements.
    /// </summary>
    /// <remarks>
    /// The property twin of <see cref="InSolutionImplementedMembers"/>, which
    /// could not cover this by widening: it filters to MethodKind.Ordinary, and
    /// a property's accessors are PropertyGet and PropertySet. So an interface
    /// declaring <c>string Channel { get; }</c> had implementers that were
    /// findable through a sibling METHOD member and not otherwise — and an
    /// interface whose members are all properties had none that were findable
    /// at all, while reporting a well-formed zero.
    /// <para>
    /// Explicit implementations are included and need no special case:
    /// FindImplementationForInterfaceMember resolves them, which matching by
    /// simple name would not, since their declared name carries the interface.
    /// </para>
    /// </remarks>
    private static List<string> InSolutionImplementedProperties(IPropertySymbol p, IReadOnlySet<string> inScope)
    {
        if (p.IsStatic) return new List<string>();

        var ids = new List<string>();
        foreach (var iface in p.ContainingType.AllInterfaces)
        {
            if (iface.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) continue;

            foreach (var member in iface.GetMembers().OfType<IPropertySymbol>())
                if (SymbolEqualityComparer.Default.Equals(
                        p.ContainingType.FindImplementationForInterfaceMember(member), p))
                    ids.Add(SymbolIds.MemberId(member));
        }
        return ids;
    }

    /// <summary>
    /// Every interface a type implements, each with the number of hops from
    /// this declaration to the one that brought it in.
    /// </summary>
    /// <remarks>
    /// Breadth-first over declared interfaces and base types, so the SHORTEST
    /// route wins: a class that names IFoo itself and also inherits a base
    /// naming it implements IFoo directly, and recording 2 there would be true
    /// of some path and wrong about the declaration.
    /// <para>
    /// A walk rather than a read of <c>AllInterfaces</c> because that property
    /// is flat. Three routes reach an interface — written here, through a base
    /// CLASS, or through a base INTERFACE — and a fix that handles only the
    /// first leaves the other two reporting a confident zero, which is the
    /// failure this whole change exists to remove.
    /// </para>
    /// <para>
    /// Out-of-solution interfaces are kept. Whether the far end is a node the
    /// graph can point at is the emitter's question, not the classifier's, and
    /// deciding it here would mean the node could not list IDisposable even as
    /// a plain fact about the declaration.
    /// </para>
    /// </remarks>
    private static List<InterfaceImplementation> InterfaceImplementations(INamedTypeSymbol symbol)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        var seen = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        var implementations = new List<InterfaceImplementation>();
        var queue = new Queue<(INamedTypeSymbol Type, int Depth)>();
        queue.Enqueue((symbol, 0));

        while (queue.Count > 0)
        {
            var (current, depth) = queue.Dequeue();
            if (!seen.Add(current)) continue;

            foreach (var iface in current.Interfaces)
            {
                // The DEFINITION, not the constructed form. A node is minted for
                // ITally<T> and never for ITally<int>, so recording what is
                // written at the use site would produce a name that matches no
                // node and an edge that is silently dropped — the generic case
                // failing while every non-generic one passed. The cost is that a
                // type implementing ITally<int> and ITally<string> lists one
                // entry, which is also all the graph has one node for.
                var name = iface.OriginalDefinition.ToDisplayString();
                if (found.Add(name)) implementations.Add(new InterfaceImplementation(name, depth + 1));

                queue.Enqueue((iface, depth + 1));
            }

            if (current.BaseType is { } baseType) queue.Enqueue((baseType, depth + 1));
        }

        return implementations;
    }

    /// <summary>
    /// Ids of the in-solution declarations this method overrides — every step
    /// up the chain, not just the immediate parent.
    /// </summary>
    /// <remarks>
    /// The class-dispatch twin of <see cref="InSolutionImplementedMembers"/>,
    /// and the join coverage was missing. A test calls the abstract
    /// declaration, the body that runs is the override, and without this the
    /// override reports zero tests — a confident "untested" for the most
    /// exercised code in a repository, since a doctrine or strategy layer is
    /// overrides all the way down.
    /// <para>
    /// The whole chain, because C# allows an override to be overridden again:
    /// a test reaching the abstract root must reach the leaf that actually
    /// runs, and stopping at one hop would report the middle of a three-deep
    /// hierarchy as covered and the leaf as untested. Out-of-solution bases
    /// stop the walk, because an edge to a framework method points at nothing.
    /// </para>
    /// </remarks>
    private static List<string> InSolutionOverriddenMembers(IMethodSymbol m, IReadOnlySet<string> inScope)
    {
        var ids = new List<string>();
        for (var baseMethod = m.OverriddenMethod; baseMethod is not null; baseMethod = baseMethod.OverriddenMethod)
        {
            if (baseMethod.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) break;
            ids.Add(SymbolIds.MethodId(baseMethod));
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
