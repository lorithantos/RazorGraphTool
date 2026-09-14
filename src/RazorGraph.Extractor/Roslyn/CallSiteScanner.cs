namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Finds the calls a scope makes: explicit invocations and new-expressions,
/// method groups handed around as delegates, initializer calls attributed to
/// the constructors that run them, implicit Dispose/DisposeAsync from using
/// constructs, and the callback targets out-of-solution code can call back
/// into.
/// </summary>
internal static class CallSiteScanner
{
    /// <summary>
    /// Call sites in one method scope: explicit invocations and new-expressions
    /// with their enclosing catch guards, plus the implicit Dispose/DisposeAsync
    /// of everything the method holds in a using. Dispose sites carry no guards
    /// on purpose — disposal runs at scope exit, where relating it to enclosing
    /// trys is subtle, and unguarded is the conservative reading for escape
    /// analysis.
    /// </summary>
    /// <param name="decl">
    /// A method declaration, or the compilation unit of a top-level program —
    /// see TopLevelProgram, which is also what decides the nodes walked for the
    /// body.
    /// </param>
    internal static IEnumerable<CallSiteInfo> MethodCallSites(
        SyntaxNode decl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        if (TopLevelProgram.DeclaredMethod(decl, model) is not { } caller) yield break;

        // An EXPLICIT static constructor is reached here — it is a
        // BaseMethodDeclarationSyntax like any other — but it is not a graph node,
        // so attributing to it would drop every call for want of a near end. The
        // same answer ConstructionSites gives: attribute to the declaring type.
        var isStaticCtor = caller.MethodKind == MethodKind.StaticConstructor;
        var fromId = isStaticCtor ? null : SymbolIds.MethodId(caller);
        var fromType = isStaticCtor ? caller.ContainingType.OriginalDefinition.ToDisplayString() : null;

        foreach (var (site, target) in TopLevelProgram.BodyNodes(decl).SelectMany(b => CallTargetSites(b, model)))
        {
            if (SymbolIds.InScopeMethodId(target, inScope) is not { } toId) continue;
            if (toId == fromId) continue; // direct recursion adds no navigational value

            var (guardedBy, filteredBy) = ExceptionFlow.SiteGuards(site, decl, model);
            yield return new CallSiteInfo(fromId, toId, guardedBy, filteredBy, FromTypeFullName: fromType);
        }

        // A method group handed somewhere is a call the future makes. The edge
        // carries no guards on purpose: a try around the registration site does
        // not catch what the delegate throws when it finally runs.
        foreach (var (_, target) in TopLevelProgram.BodyNodes(decl).SelectMany(b => MethodGroupSites(b, model)))
        {
            if (SymbolIds.InScopeMethodId(target, inScope) is not { } toId) continue;
            if (toId == fromId) continue;

            yield return new CallSiteInfo(
                fromId, toId, Array.Empty<string>(), Array.Empty<string>(), IsDelegate: true, FromTypeFullName: fromType);
        }

        foreach (var (resourceType, isAsync) in DisposedResources(decl, model))
        {
            if (ResolveDisposeMethod(resourceType, isAsync) is not { } dispose) continue;
            if (SymbolIds.InScopeMethodId(dispose, inScope) is not { } toId) continue;
            if (toId == fromId) continue;

            yield return new CallSiteInfo(
                fromId, toId, Array.Empty<string>(), Array.Empty<string>(), FromTypeFullName: fromType);
        }
    }

    /// <summary>
    /// In-solution types built by new-expressions anywhere under a syntax node.
    /// </summary>
    /// <remarks>
    /// <c>BaseObjectCreationExpressionSyntax</c> is exactly two shapes —
    /// <c>new Foo()</c> and target-typed <c>new()</c> — and an object initialiser
    /// hangs off the creation node rather than being one, so a type built with
    /// <c>new Foo { A = 1 }</c> is counted once and not twice.
    /// <para>
    /// Three shapes are deliberately absent, and each is absent for a reason
    /// rather than by oversight. <c>new T()</c> on a type parameter resolves to
    /// no constructor symbol at all, so there is nothing to point at and an edge
    /// to the constraint would name a type the call site does not build.
    /// <c>typeof(X)</c> is not a creation node — it is a different claim, and
    /// Registers already carries it. Anonymous types resolve to a constructor but
    /// have no declaration and therefore no node.
    /// </para>
    /// <para>
    /// Reduced to OriginalDefinition because a node is minted for the DECLARATION:
    /// <c>new Repo&lt;Order&gt;()</c> must find <c>Repo&lt;T&gt;</c>, and keeping the
    /// constructed form would mint an id no declaration ever produces — the
    /// generic case failing silently while every other one passed.
    /// </para>
    /// </remarks>
    private static IEnumerable<(SyntaxNode Site, INamedTypeSymbol Constructed)> ConstructedTypes(
        SyntaxNode scope, SemanticModel model, IReadOnlySet<string> inScope)
    {
        foreach (var node in scope.DescendantNodesAndSelf())
        {
            if (node is not BaseObjectCreationExpressionSyntax) continue;
            if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol ctor) continue;

            var constructed = ctor.ContainingType.OriginalDefinition;
            if (constructed.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) continue;

            yield return (node, constructed);
        }
    }

    /// <summary>
    /// Construction sites in one method declaration, attributed to that method.
    /// </summary>
    /// <remarks>
    /// Lambdas and local functions are ordinary descendants of the declaration
    /// rather than scopes of their own, so a factory lambda's new-expression is
    /// attributed to the method holding it — which is the method a reader would
    /// go and open. Accessor bodies are NOT reached, because MethodScopes
    /// enumerates BaseMethodDeclarationSyntax and an accessor is not one; that is
    /// a pre-existing limit of the walk, shared with Calls edges.
    /// </remarks>
    internal static IEnumerable<ConstructionSiteInfo> ConstructionSites(
        SyntaxNode decl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        if (TopLevelProgram.DeclaredMethod(decl, model) is not { } caller) yield break;

        // An EXPLICIT static constructor is reached here — it is a
        // BaseMethodDeclarationSyntax like any other — but it is not a graph node,
        // so attributing to it would drop every edge for want of a near end, which
        // is the same failure the whole Constructs edge exists to remove. It gets
        // the same answer as the implicit one: attribute to the declaring type.
        var isStaticCtor = caller.MethodKind == MethodKind.StaticConstructor;
        var fromId = isStaticCtor ? null : SymbolIds.MethodId(caller);
        var fromType = isStaticCtor ? caller.ContainingType.OriginalDefinition.ToDisplayString() : null;

        foreach (var (site, constructed) in TopLevelProgram.BodyNodes(decl)
                     .SelectMany(body => ConstructedTypes(body, model, inScope)))
            yield return new ConstructionSiteInfo(fromId, constructed.ToDisplayString(), LineOf(site), fromType);
    }

    /// <summary>
    /// Construction sites in one type's STATIC field and property initializers,
    /// attributed to the declaring type.
    /// </summary>
    /// <remarks>
    /// The composition-root idiom the instance walk cannot see:
    /// <c>private static readonly IFoo Default = new Foo();</c> builds a type that
    /// otherwise has no incoming edge of any kind and reads as dead.
    /// <para>
    /// The near end is the TYPE and not a member, which makes this the one shape
    /// of Constructs edge whose source is not a method. That is deliberate and
    /// cheaper than the alternative: a static constructor is not a node, and
    /// minting one would name nothing a reader can open while adding every static
    /// ctor in the solution to uncovered_methods as a confident untested claim.
    /// Responses carry fromType, so a reader can tell the two shapes apart.
    /// </para>
    /// </remarks>
    internal static IEnumerable<ConstructionSiteInfo> StaticInitializerConstructionSites(
        TypeDeclarationSyntax typeDecl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        var initializers = TypeInitializers.StaticInitializers(typeDecl).ToList();
        if (initializers.Count == 0) yield break;
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) yield break;

        var fromType = type.OriginalDefinition.ToDisplayString();

        foreach (var initializer in initializers)
            foreach (var (site, constructed) in ConstructedTypes(initializer, model, inScope))
                yield return new ConstructionSiteInfo(null, constructed.ToDisplayString(), LineOf(site), fromType);
    }

    /// <summary>
    /// Construction sites in one type's field and property initializers,
    /// attributed to every instance constructor — the code that actually runs
    /// them. The twin of <see cref="InitializerCallSites"/>, and it matters more
    /// here: `private readonly IFoo _foo = new Foo();` IS the composition root
    /// shape this edge exists to make visible.
    /// </summary>
    /// <remarks>
    /// The implicit default constructor is a node whenever a type has instance
    /// initializers, which is exactly the condition under which this method
    /// yields anything — so the far end of these edges is present for the same
    /// reason the near end is.
    /// </remarks>
    internal static IEnumerable<ConstructionSiteInfo> InitializerConstructionSites(
        TypeDeclarationSyntax typeDecl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        var initializers = TypeInitializers.InstanceInitializers(typeDecl).ToList();
        if (initializers.Count == 0) yield break;
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) yield break;

        var ctorIds = type.InstanceConstructors.Select(SymbolIds.MethodId).Distinct().ToList();

        foreach (var initializer in initializers)
            foreach (var (site, constructed) in ConstructedTypes(initializer, model, inScope))
                foreach (var ctorId in ctorIds)
                    yield return new ConstructionSiteInfo(ctorId, constructed.ToDisplayString(), LineOf(site));
    }

    private static int LineOf(SyntaxNode node) =>
        node.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

    /// <summary>
    /// Call sites for one type's field and property initializers. Initializers
    /// execute inside the instance constructors, not inside any method
    /// declaration, so the method walk never sees them. Their calls are
    /// attributed to every instance ctor of the type — including the implicit
    /// default ctor, which in this case is exactly the code that runs them.
    /// (Overapproximation: a ctor chaining this(...) does not rerun
    /// initializers, but the work is still one ctor away.) An initializer
    /// cannot contain a try, so the sites are always unguarded.
    /// </summary>
    internal static IEnumerable<CallSiteInfo> InitializerCallSites(
        TypeDeclarationSyntax typeDecl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        var initializers = TypeInitializers.InstanceInitializers(typeDecl).ToList();
        if (initializers.Count == 0) yield break;
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) yield break;

        var ctorIds = type.InstanceConstructors.Select(SymbolIds.MethodId).Distinct().ToList();

        foreach (var initializer in initializers)
            foreach (var (_, target) in CallTargetSites(initializer, model))
            {
                if (SymbolIds.InScopeMethodId(target, inScope) is not { } toId) continue;

                foreach (var ctorId in ctorIds)
                    if (toId != ctorId)
                        yield return new CallSiteInfo(
                            ctorId, toId, Array.Empty<string>(), Array.Empty<string>());
            }
    }

    /// <summary>
    /// Call sites in one type's STATIC field and property initializers,
    /// attributed to the declaring type — the twin of
    /// <see cref="StaticInitializerConstructionSites"/>, and the gap it left:
    /// <c>private static readonly Board Drawn = DrawnBoard();</c> invokes a
    /// method that otherwise has no caller and reads as dead.
    /// </summary>
    /// <remarks>
    /// The near end is the TYPE and not a member, for the reason recorded on the
    /// construction twin: a static constructor is not a node, and minting one
    /// would name nothing a reader can open while adding every static ctor in
    /// the solution to uncovered_methods as a confident untested claim. An
    /// initializer cannot contain a try, so the sites are always unguarded.
    /// </remarks>
    internal static IEnumerable<CallSiteInfo> StaticInitializerCallSites(
        TypeDeclarationSyntax typeDecl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        var initializers = TypeInitializers.StaticInitializers(typeDecl).ToList();
        if (initializers.Count == 0) yield break;
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) yield break;

        var fromType = type.OriginalDefinition.ToDisplayString();

        foreach (var initializer in initializers)
            foreach (var (_, target) in CallTargetSites(initializer, model))
            {
                if (SymbolIds.InScopeMethodId(target, inScope) is not { } toId) continue;

                yield return new CallSiteInfo(
                    null, toId, Array.Empty<string>(), Array.Empty<string>(), FromTypeFullName: fromType);
            }
    }

    /// <summary>
    /// Callback targets in one method declaration: the target of a method
    /// group, or the container of a lambda, handed to a receiver declared
    /// outside the loaded projects — a framework ctor or method argument, or
    /// an assignment to an external property or event. The container stands in
    /// for its lambdas because a lambda has no node of its own; its calls and
    /// throws are already attributed there, so marking the container makes
    /// exactly those facts escapable (a deliberate over-approximation — the
    /// container's other throws ride along). One hop only: a delegate stored
    /// and forwarded elsewhere is not tracked.
    /// </summary>
    internal static IEnumerable<string> CallbackTargets(
        SyntaxNode decl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        if (TopLevelProgram.DeclaredMethod(decl, model) is not { } container) yield break;

        foreach (var (site, target) in TopLevelProgram.BodyNodes(decl).SelectMany(b => MethodGroupSites(b, model)))
        {
            if (!RegisteredOutOfSolution(site, model, inScope)) continue;
            if (SymbolIds.InScopeMethodId(target, inScope) is { } targetId) yield return targetId;
        }

        foreach (var lambda in TopLevelProgram.BodyNodes(decl)
            .SelectMany(b => b.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>()))
        {
            if (model.GetTypeInfo(lambda).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                continue;
            if (RegisteredOutOfSolution(lambda, model, inScope))
            {
                yield return SymbolIds.MethodId(container);
                break; // one stamp per container is enough
            }
        }
    }

    /// <summary>
    /// Every method a scope calls, with the syntax node of each site: explicit
    /// invocations, plus new-expressions — a constructor call with no
    /// invocation syntax at the site. CandidateSymbols covers calls the
    /// compiler could not fully bind (an overload set narrowed by a dynamic or
    /// erroneous argument).
    /// </summary>
    internal static IEnumerable<(SyntaxNode Site, IMethodSymbol Target)> CallTargetSites(
        SyntaxNode scope, SemanticModel model)
    {
        foreach (var node in scope.DescendantNodesAndSelf())
        {
            if (node is not InvocationExpressionSyntax and not BaseObjectCreationExpressionSyntax)
                continue;

            var info = model.GetSymbolInfo(node);
            var target = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (target != null) yield return (node, target);
        }
    }

    /// <summary>
    /// Method-group references in one scope — an identifier or member access
    /// naming a method where a delegate is expected. The conversion type is
    /// the discriminator: a real method group converts to a delegate type,
    /// which is exactly what excludes nameof and plain member reads.
    /// </summary>
    internal static IEnumerable<(SyntaxNode Site, IMethodSymbol Target)> MethodGroupSites(
        SyntaxNode scope, SemanticModel model)
    {
        foreach (var node in scope.DescendantNodesAndSelf())
        {
            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                continue;

            // The name inside an invocation or member access is not itself a
            // method group; only the outermost expression converts.
            if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
                continue;
            if (node.Parent is MemberAccessExpressionSyntax) continue;

            if (model.GetTypeInfo(node).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                continue;

            var info = model.GetSymbolInfo(node);
            var target = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (target != null) yield return (node, target);
        }
    }

    /// <summary>
    /// Whether a delegate-valued expression lands in out-of-solution hands:
    /// an argument to an external invocation or construction, or the right
    /// side of an assignment (including +=) whose left side is external.
    /// Anything else — a local, a field of this solution, a return — stays
    /// in-solution and answers false: the delegate edge still connects the
    /// chain, it just is not a framework entry surface.
    /// </summary>
    private static bool RegisteredOutOfSolution(
        SyntaxNode registration, SemanticModel model, IReadOnlySet<string> inScope)
    {
        for (SyntaxNode? node = registration; node is not null and not StatementSyntax; node = node.Parent)
        {
            switch (node.Parent)
            {
                case ArgumentSyntax argument:
                    var call = argument.Ancestors().FirstOrDefault(a =>
                        a is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);
                    if (call == null) return false;
                    var callInfo = model.GetSymbolInfo(call);
                    var callee = callInfo.Symbol as IMethodSymbol
                        ?? callInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    return SymbolOutOfSolution(callee, inScope);

                case AssignmentExpressionSyntax assignment when assignment.Right == node:
                    return SymbolOutOfSolution(model.GetSymbolInfo(assignment.Left).Symbol, inScope);

                case EqualsValueClauseSyntax:
                    return false; // initializing a local or field of this solution
            }
        }
        return false;
    }

    private static bool SymbolOutOfSolution(ISymbol? symbol, IReadOnlySet<string> inScope) =>
        symbol?.ContainingAssembly?.Name is { } assembly && !inScope.Contains(assembly);

    /// <summary>
    /// Every resource a method disposes implicitly: using statements (block and
    /// expression forms) and using declarations, with await variants marked so
    /// the caller resolves DisposeAsync rather than Dispose.
    /// </summary>
    private static IEnumerable<DisposedResource> DisposedResources(
        SyntaxNode decl, SemanticModel model)
    {
        foreach (var node in TopLevelProgram.BodyNodes(decl).SelectMany(b => b.DescendantNodes()))
        {
            switch (node)
            {
                case UsingStatementSyntax u:
                    foreach (var resource in UsingStatementResources(u, model))
                        yield return resource;
                    break;

                case LocalDeclarationStatementSyntax l when l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                    foreach (var resource in DeclaredResources(l.Declaration, model, l.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)))
                        yield return resource;
                    break;
            }
        }
    }

    /// <summary>
    /// Resources of one using statement: the declared variables when it has a
    /// declaration, otherwise the expression form's single resource.
    /// </summary>
    private static IEnumerable<DisposedResource> UsingStatementResources(
        UsingStatementSyntax u, SemanticModel model)
    {
        var isAsync = u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);

        if (u.Declaration != null)
        {
            foreach (var resource in DeclaredResources(u.Declaration, model, isAsync))
                yield return resource;
            yield break;
        }

        if (u.Expression != null && model.GetTypeInfo(u.Expression).Type is { } expressionType)
            yield return new DisposedResource(expressionType, isAsync);
    }

    /// <summary>Typed locals of one using declaration's variable list.</summary>
    private static IEnumerable<DisposedResource> DeclaredResources(
        VariableDeclarationSyntax declaration, SemanticModel model, bool isAsync)
    {
        foreach (var variable in declaration.Variables)
            if (model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                yield return new DisposedResource(local.Type, isAsync);
    }

    /// <summary>
    /// The Dispose/DisposeAsync a using construct actually runs: the interface
    /// implementation when the type is IDisposable/IAsyncDisposable, otherwise a
    /// parameterless method found by shape — ref structs and pattern-based
    /// disposal bind by name, not interface.
    /// </summary>
    private static IMethodSymbol? ResolveDisposeMethod(ITypeSymbol type, bool isAsync)
    {
        var interfaceName = isAsync ? "IAsyncDisposable" : "IDisposable";
        var methodName = isAsync ? "DisposeAsync" : "Dispose";

        var interfaceMember = type.AllInterfaces
            .FirstOrDefault(i => i.Name == interfaceName && i.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true })
            ?.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMember != null && type.FindImplementationForInterfaceMember(interfaceMember) is IMethodSymbol implementation)
            return implementation;

        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            var byShape = t.GetMembers(methodName).OfType<IMethodSymbol>()
                .FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0);
            if (byShape != null) return byShape;
        }
        return null;
    }
}
