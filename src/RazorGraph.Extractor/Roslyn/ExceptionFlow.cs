namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// What can leave a method and what stands in the way: local throw analysis
/// with catch matching, the catch sets an HTTP boundary absorbs, and the
/// guards enclosing a call site.
/// </summary>
internal static class ExceptionFlow
{
    /// <summary>
    /// The exception types that can leave this method locally: throw statements
    /// and throw expressions whose type no enclosing catch of the same method
    /// handles firmly. A bare rethrow contributes its catch's declared type —
    /// System.Exception for an untyped catch, the honest upper bound. Throws
    /// inside lambdas are attributed here as conditional — the lambda runs
    /// when the delegate runs, not when this method does, but the container is
    /// the only node it has. Local functions stay skipped: declared is not
    /// registered, and a dead local function would over-report. Assignability
    /// is decided on live symbols now, while both sides still are symbols —
    /// never by name at query time.
    /// </summary>
    internal static List<ThrownType> ExtractThrows(BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var sites = new List<(ITypeSymbol? Type, SyntaxNode Site, bool InLambda)>();

        foreach (var node in decl.DescendantNodes(n => n == decl || n is not LocalFunctionStatementSyntax))
        {
            var inLambda = node.Ancestors().TakeWhile(a => a != decl)
                .Any(a => a is AnonymousFunctionExpressionSyntax);

            switch (node)
            {
                case ThrowStatementSyntax { Expression: { } thrown } statement:
                    sites.Add((model.GetTypeInfo(thrown).Type, statement, inLambda));
                    break;

                case ThrowStatementSyntax rethrow:
                    sites.Add((RethrownType(rethrow, decl, model), rethrow, inLambda));
                    break;

                case ThrowExpressionSyntax { Expression: { } thrown } expression:
                    sites.Add((model.GetTypeInfo(thrown).Type, expression, inLambda));
                    break;
            }
        }

        var escaping = new List<ThrownType>();

        foreach (var (type, site, inLambda) in sites)
        {
            if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error) continue;

            var conditional = inLambda;
            var handled = false;

            // The walk stops at a lambda boundary as well as at the method: a
            // try in the container encloses the lambda's *creation*, not its
            // later execution, so only trys inside the same lambda guard it.
            foreach (var tryStatement in site.Ancestors()
                .TakeWhile(a => a != decl && a is not AnonymousFunctionExpressionSyntax)
                .OfType<TryStatementSyntax>())
            {
                // A throw inside a catch or finally answers to the outer trys only.
                if (!tryStatement.Block.Span.Contains(site.Span)) continue;

                foreach (var catchClause in tryStatement.Catches)
                {
                    if (!CatchMatches(catchClause, named, model)) continue;
                    if (catchClause.Filter == null) { handled = true; break; }
                    conditional = true; // the filter may decline at runtime
                }
                if (handled) break;
            }

            if (!handled)
                escaping.Add(new ThrownType(named.ToDisplayString(), AncestorChain(named), conditional));
        }

        // One record per type; an unconditional escape outranks a conditional one.
        return escaping
            .GroupBy(t => t.Type, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Conditional).First())
            .ToList();
    }

    /// <summary>The type a bare 'throw;' rethrows: its catch's declaration, else System.Exception.</summary>
    private static ITypeSymbol? RethrownType(
        ThrowStatementSyntax rethrow, BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var catchClause = rethrow.Ancestors()
            .TakeWhile(a => a != decl && a is not AnonymousFunctionExpressionSyntax)
            .OfType<CatchClauseSyntax>().FirstOrDefault();
        return catchClause?.Declaration?.Type is { } typeSyntax
            ? model.GetTypeInfo(typeSyntax).Type
            : model.Compilation.GetTypeByMetadataName("System.Exception");
    }

    private static bool CatchMatches(CatchClauseSyntax catchClause, INamedTypeSymbol thrown, SemanticModel model)
    {
        if (catchClause.Declaration?.Type is not { } typeSyntax) return true; // untyped catch-all
        if (model.GetTypeInfo(typeSyntax).Type is not INamedTypeSymbol caught) return false;

        for (ITypeSymbol? t = thrown; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, caught)) return true;
        return false;
    }

    /// <summary>Self first, base types after, stopping at System.Exception — what a catch set is matched against.</summary>
    private static List<string> AncestorChain(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            var name = t.ToDisplayString();
            chain.Add(name);
            if (name == "System.Exception") break;
        }
        return chain;
    }

    /// <summary>
    /// The exception types an HTTP boundary method deliberately absorbs. A
    /// middleware whose catch takes a type is converting that exception into a
    /// shaped response — design, not crash — so escapes it would intercept are
    /// reported with that disposition rather than as raw failures. Catch sets
    /// come from the method's own catch clauses (lambdas excluded);
    /// IExceptionHandler has no syntactic catch — what it handles is a runtime
    /// decision — so it records as a conditional catch-all.
    /// </summary>
    internal static (List<string> Firm, List<string> Filtered) BoundaryCatchSets(
        IMethodSymbol m, BaseMethodDeclarationSyntax? declSyntax, SemanticModel? model)
    {
        var none = (new List<string>(), new List<string>());

        if (MethodRoles.IsExceptionHandlerShape(m))
            return (new List<string>(), new List<string> { "*" });
        if (!MethodRoles.IsHttpMiddlewareShape(m) || declSyntax == null || model == null) return none;

        var firm = new List<string>();
        var filtered = new List<string>();

        foreach (var catchClause in declSyntax
            .DescendantNodes(n => n == declSyntax
                || n is not (AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax))
            .OfType<CatchClauseSyntax>())
        {
            string caught;
            if (catchClause.Declaration?.Type is { } typeSyntax)
            {
                if (model.GetTypeInfo(typeSyntax).Type is not { } caughtType) continue;
                caught = caughtType.ToDisplayString();
            }
            else
            {
                caught = "*";
            }

            (catchClause.Filter == null ? firm : filtered).Add(caught);
        }

        return (firm, filtered);
    }

    /// <summary>
    /// The catch guards enclosing one call site: types caught firmly by
    /// enclosing trys ("*" for an untyped catch-all), and types caught only
    /// behind a when filter — a filter may decline at runtime, so it counts as
    /// conditional handling, never as handling. Only trys whose *block* holds
    /// the site guard it; a site inside a catch or finally answers to the
    /// outer trys alone.
    /// </summary>
    internal static (List<string> GuardedBy, List<string> FilteredBy) SiteGuards(
        SyntaxNode site, BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var guardedBy = new List<string>();
        var filteredBy = new List<string>();

        foreach (var tryStatement in site.Ancestors().TakeWhile(a => a != decl).OfType<TryStatementSyntax>())
        {
            if (!tryStatement.Block.Span.Contains(site.Span)) continue;

            foreach (var catchClause in tryStatement.Catches)
            {
                string caught;
                if (catchClause.Declaration?.Type is { } typeSyntax)
                {
                    if (model.GetTypeInfo(typeSyntax).Type is not { } caughtType) continue;
                    caught = caughtType.ToDisplayString();
                }
                else
                {
                    caught = "*";
                }

                (catchClause.Filter == null ? guardedBy : filteredBy).Add(caught);
            }
        }

        return (guardedBy, filteredBy);
    }
}
