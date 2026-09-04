namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Finds every read and write of an in-solution property or field in a syntax
/// tree, attributed to the code that performs it — the mechanism behind Reads
/// and Writes edges.
/// </summary>
internal static class MemberAccessScanner
{
    /// <summary>
    /// Every read and write of an in-solution property or field in one tree,
    /// attributed to the code that performs it: the enclosing method or
    /// constructor; the enclosing property for accessor and expression bodies
    /// (a computed property that reads another member is real data flow, and
    /// accessors are not Method nodes); and the instance constructors for
    /// member-initializer accesses, mirroring CallSiteScanner's initializer
    /// attribution. Static-member initializers are skipped for the same reason
    /// static ctors are not nodes. nameof arguments are names, not accesses,
    /// and are excluded — same reasoning as stripping JS comments before
    /// selector scanning: mentioning a thing must not create a data-flow
    /// contract.
    /// </summary>
    internal static IEnumerable<MemberAccessInfo> TreeMemberAccesses(
        SyntaxNode root, SemanticModel model, IReadOnlySet<string> inScope)
    {
        foreach (var name in root.DescendantNodes().OfType<IdentifierNameSyntax>())
        {
            var symbol = model.GetSymbolInfo(name).Symbol;
            if (symbol is not (IPropertySymbol or IFieldSymbol)) continue;
            if (symbol.IsImplicitlyDeclared) continue;
            if (symbol is IFieldSymbol { AssociatedSymbol: not null }) continue;

            var def = symbol.OriginalDefinition;
            if (def.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) continue;
            if (InsideNameof(name)) continue;

            var toId = SymbolIds.MemberId(def);
            var (isRead, isWrite) = ClassifyAccess(TopAccessExpression(name));

            foreach (var fromId in AccessAttribution(name, model))
            {
                if (fromId == toId) continue; // self-access adds no navigational value
                yield return new MemberAccessInfo(fromId, toId, isRead, isWrite);
            }
        }
    }

    /// <summary>
    /// The node ids an access at <paramref name="site"/> belongs to. Usually
    /// one; every instance ctor for a member-initializer access (the same
    /// overapproximation CallSiteScanner.InitializerCallSites documents); none
    /// when the access sits somewhere with no node to own it.
    /// </summary>
    private static IEnumerable<string> AccessAttribution(SyntaxNode site, SemanticModel model)
    {
        for (var node = site.Parent; node != null; node = node.Parent)
        {
            switch (node)
            {
                // A member initializer runs in the instance ctors, not in the
                // member it initializes. Locals' and parameters' EqualsValue
                // clauses fall through to the enclosing method instead.
                case EqualsValueClauseSyntax { Parent: PropertyDeclarationSyntax prop } clause
                    when prop.Initializer == clause:
                    return prop.Modifiers.Any(SyntaxKind.StaticKeyword)
                        ? Enumerable.Empty<string>()
                        : InstanceCtorIds(prop, model);

                case EqualsValueClauseSyntax { Parent: VariableDeclaratorSyntax { Parent.Parent: FieldDeclarationSyntax field } }:
                    return field.Modifiers.Any(SyntaxKind.StaticKeyword)
                        ? Enumerable.Empty<string>()
                        : InstanceCtorIds(field, model);

                case BaseMethodDeclarationSyntax methodDecl:
                    return model.GetDeclaredSymbol(methodDecl) is IMethodSymbol m
                        ? new[] { SymbolIds.MethodId(m) }
                        : Enumerable.Empty<string>();

                // Accessor bodies, expression bodies, and indexers all live
                // under a BasePropertyDeclaration. Events land here too and
                // yield nothing — they are not graph nodes.
                case BasePropertyDeclarationSyntax propDecl:
                    return model.GetDeclaredSymbol(propDecl) is IPropertySymbol p
                        ? new[] { SymbolIds.MemberId(p) }
                        : Enumerable.Empty<string>();

                // Reaching the compilation unit means no member declaration owns
                // the access — it is a top-level statement, and the code that
                // runs it is the synthesized entry point. Last case in the walk,
                // so an access inside any real member has already returned; and
                // DeclaredMethod answers null for a unit with no global
                // statements, which is every ordinary file.
                case CompilationUnitSyntax unit:
                    return TopLevelProgram.DeclaredMethod(unit, model) is { } entryPoint
                        ? new[] { SymbolIds.MethodId(entryPoint) }
                        : Enumerable.Empty<string>();
            }
        }
        return Enumerable.Empty<string>();
    }

    private static IEnumerable<string> InstanceCtorIds(SyntaxNode memberDecl, SemanticModel model) =>
        memberDecl.FirstAncestorOrSelf<TypeDeclarationSyntax>() is { } typeDecl
        && model.GetDeclaredSymbol(typeDecl) is INamedTypeSymbol type
            ? type.InstanceConstructors.Select(SymbolIds.MethodId).Distinct()
            : Enumerable.Empty<string>();

    /// <summary>
    /// The outermost expression this name participates in as a value: the
    /// member access for a.B, the conditional access for a?.B. Assignment
    /// context lives on that expression's parent, not the name's.
    /// </summary>
    private static ExpressionSyntax TopAccessExpression(IdentifierNameSyntax name)
    {
        ExpressionSyntax expr = name;
        while (true)
        {
            if (expr.Parent is MemberAccessExpressionSyntax ma && ma.Name == expr) { expr = ma; continue; }
            if (expr.Parent is MemberBindingExpressionSyntax mb && mb.Name == expr) { expr = mb; continue; }
            if (expr.Parent is ConditionalAccessExpressionSyntax ca && ca.WhenNotNull == expr) { expr = ca; continue; }
            return expr;
        }
    }

    /// <summary>
    /// Read, write, or both, from syntactic position. Compound assignment and
    /// increment read the old value before writing; an out argument writes
    /// blind; a ref argument must be assumed to do both. Anything
    /// unrecognized is a read — the direction that never invents a mutation.
    /// </summary>
    private static (bool IsRead, bool IsWrite) ClassifyAccess(ExpressionSyntax expr) =>
        expr.Parent switch
        {
            AssignmentExpressionSyntax assign when assign.Left == expr =>
                assign.IsKind(SyntaxKind.SimpleAssignmentExpression) ? (false, true) : (true, true),
            PrefixUnaryExpressionSyntax pre when
                pre.IsKind(SyntaxKind.PreIncrementExpression) || pre.IsKind(SyntaxKind.PreDecrementExpression) => (true, true),
            PostfixUnaryExpressionSyntax post when
                post.IsKind(SyntaxKind.PostIncrementExpression) || post.IsKind(SyntaxKind.PostDecrementExpression) => (true, true),
            ArgumentSyntax arg when arg.RefOrOutKeyword.IsKind(SyntaxKind.OutKeyword) => (false, true),
            ArgumentSyntax arg when arg.RefOrOutKeyword.IsKind(SyntaxKind.RefKeyword) => (true, true),
            _ => (true, false)
        };

    private static bool InsideNameof(SyntaxNode node) =>
        node.Ancestors().OfType<InvocationExpressionSyntax>()
            .Any(inv => inv.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" });
}
