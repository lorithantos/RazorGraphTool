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
    /// <remarks>
    /// An indexer is reached through an element access -- rack[0], rack?[0],
    /// or [0] = x in an object initializer -- which names no identifier, so
    /// those sites are accesses of the indexer itself, classified by the same
    /// position rules as a named member.
    /// </remarks>
    internal static IEnumerable<MemberAccessInfo> TreeMemberAccesses(
        SyntaxNode root, SemanticModel model, IReadOnlySet<string> inScope)
    {
        foreach (var node in root.DescendantNodes())
        {
            if (AccessedMember(node, model) is not { } symbol) continue;
            if (symbol.IsImplicitlyDeclared) continue;

            var def = symbol.OriginalDefinition;
            if (def.ContainingAssembly?.Name is not { } assembly || !inScope.Contains(assembly)) continue;
            if (InsideNameof(node)) continue;

            var toId = SymbolIds.MemberId(def);
            var (isRead, isWrite) = ClassifyAccess(TopAccessExpression(node));

            foreach (var fromId in SyntaxAttribution.OwningNodeIds(node, model))
            {
                if (fromId == toId) continue; // self-access adds no navigational value
                yield return new MemberAccessInfo(fromId, toId, isRead, isWrite);
            }
        }
    }

    /// <summary>
    /// The property or field a node accesses: a name bound to one, or an
    /// element access bound to an indexer. Null for everything else, arrays
    /// and pointers included -- their element access binds to no member.
    /// </summary>
    private static ISymbol? AccessedMember(SyntaxNode node, SemanticModel model) =>
        node switch
        {
            IdentifierNameSyntax name => model.GetSymbolInfo(name).Symbol switch
            {
                IFieldSymbol { AssociatedSymbol: not null } => null, // a property's backing field
                IPropertySymbol property => property,
                IFieldSymbol field => field,
                _ => null
            },
            ElementAccessExpressionSyntax or ElementBindingExpressionSyntax or ImplicitElementAccessSyntax =>
                model.GetSymbolInfo((ExpressionSyntax)node).Symbol is IPropertySymbol { IsIndexer: true } indexer
                    ? indexer
                    : null,
            _ => null
        };

    /// <summary>
    /// The outermost expression this access participates in as a value: the
    /// member access for a.B, the conditional access for a?.B and a?[0].
    /// Assignment context lives on that expression's parent, not the access's.
    /// </summary>
    private static ExpressionSyntax TopAccessExpression(SyntaxNode access)
    {
        var expr = (ExpressionSyntax)access;
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
