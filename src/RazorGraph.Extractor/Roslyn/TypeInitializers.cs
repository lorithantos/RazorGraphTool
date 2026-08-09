namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Instance field and property initializers of a type, and the symbol-side
/// gate for the implicit-ctor decision. Initializers execute inside the
/// instance constructors, which is why both the method-node decision and
/// call-site attribution consult the same enumeration.
/// </summary>
internal static class TypeInitializers
{
    /// <summary>Symbol-side gate for the implicit-ctor node decision.</summary>
    internal static bool HasInstanceInitializers(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(t => InstanceInitializers(t).Any());

    /// <summary>
    /// Instance field and property initializers of one type declaration. Static
    /// initializers are excluded on purpose: they run in the static ctor, which
    /// has no syntactic call sites and is not a graph node.
    /// </summary>
    internal static IEnumerable<EqualsValueClauseSyntax> InstanceInitializers(TypeDeclarationSyntax typeDecl)
    {
        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f when !f.Modifiers.Any(SyntaxKind.StaticKeyword):
                    foreach (var variable in f.Declaration.Variables)
                        if (variable.Initializer != null)
                            yield return variable.Initializer;
                    break;

                case PropertyDeclarationSyntax p when !p.Modifiers.Any(SyntaxKind.StaticKeyword) && p.Initializer != null:
                    yield return p.Initializer;
                    break;
            }
        }
    }
}
