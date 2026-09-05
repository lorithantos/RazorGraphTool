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

            foreach (var fromId in SyntaxAttribution.OwningNodeIds(name, model))
            {
                if (fromId == toId) continue; // self-access adds no navigational value
                yield return new MemberAccessInfo(fromId, toId, isRead, isWrite);
            }
        }
    }

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
