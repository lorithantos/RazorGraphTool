namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Which graph node owns a position in source. Shared because every scanner
/// that attributes something to "the code that does it" -- a member access, a
/// quoted name -- has to answer the same question, and answering it twice is
/// how two scanners come to disagree about where a member initializer runs.
/// </summary>
internal static class SyntaxAttribution
{
    /// <summary>
    /// The node ids a position belongs to. Usually one; every instance
    /// constructor for a member-initializer position, because that is where
    /// the initializer actually runs; none when nothing owns it.
    /// </summary>
    internal static IEnumerable<string> OwningNodeIds(SyntaxNode site, SemanticModel model)
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
                // yield nothing -- they are not graph nodes.
                case BasePropertyDeclarationSyntax propDecl:
                    return model.GetDeclaredSymbol(propDecl) is IPropertySymbol p
                        ? new[] { SymbolIds.MemberId(p) }
                        : Enumerable.Empty<string>();

                // Reaching the compilation unit means no member declaration owns
                // the position -- it is a top-level statement, and the code that
                // runs it is the synthesized entry point. Last case in the walk,
                // so a position inside any real member has already returned; and
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
}
