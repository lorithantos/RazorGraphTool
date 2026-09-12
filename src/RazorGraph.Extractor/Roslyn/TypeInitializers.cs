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
    /// has no syntactic call sites and is not a graph node. See
    /// <see cref="StaticInitializers"/>, which the Constructs edge uses and which
    /// attributes to the declaring type for exactly that reason.
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

    /// <summary>
    /// Static field and property initializers of one type declaration — the
    /// mirror of <see cref="InstanceInitializers"/>.
    /// </summary>
    /// <remarks>
    /// These run in the static constructor, and a consumer CANNOT attribute them
    /// to a member the way the instance walk does. A static ctor is not a graph
    /// node, and minting one would be worse than the gap it closes: an implicit
    /// static ctor has no syntax at all, so the node would name nothing a reader
    /// can open, and nothing models "first access runs it", so every static ctor
    /// in a solution would arrive in uncovered_methods as a confident untested
    /// claim. Consumers attribute these to the DECLARING TYPE instead.
    /// <para>
    /// `private static readonly IFoo Default = new Foo();` is a common
    /// composition-root idiom, and before this existed the type it builds had no
    /// incoming edge of any kind.
    /// </para>
    /// </remarks>
    internal static IEnumerable<EqualsValueClauseSyntax> StaticInitializers(TypeDeclarationSyntax typeDecl)
    {
        foreach (var member in typeDecl.Members)
        {
            switch (member)
            {
                case FieldDeclarationSyntax f when f.Modifiers.Any(SyntaxKind.StaticKeyword):
                    foreach (var variable in f.Declaration.Variables)
                        if (variable.Initializer != null)
                            yield return variable.Initializer;
                    break;

                case PropertyDeclarationSyntax p when p.Modifiers.Any(SyntaxKind.StaticKeyword) && p.Initializer != null:
                    yield return p.Initializer;
                    break;
            }
        }
    }
}
