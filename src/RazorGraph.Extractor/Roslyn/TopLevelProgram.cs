namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Top-level statements as a method scope.
///
/// A file of top-level statements declares neither a type nor a method: the
/// compiler synthesizes a Program class holding a &lt;Main&gt;$ method, and
/// neither carries a TypeDeclarationSyntax or a BaseMethodDeclarationSyntax for
/// the extraction passes to find. Every pass in this family walks one or the
/// other, so before this class a top-level program contributed *nothing* — no
/// node for the entry point, and no edge out of it. The consequence was worse
/// than a missing node: a method called only from Main had no callers in the
/// graph and so read exactly like dead code, which is the opposite of the truth
/// about the one method the whole program starts from.
///
/// The scope handed to the walkers is the global statements, never the whole
/// compilation unit. A file may declare types *after* its top-level statements,
/// and those bodies are already walked as method declarations; walking the unit
/// would attribute their calls and throws to Main a second time.
/// </summary>
internal static class TopLevelProgram
{
    /// <summary>
    /// The compilation unit whose global statements form an entry-point body,
    /// or null when this tree is ordinary code. A compilation has at most one
    /// such file — the language enforces it — so callers never have to merge
    /// two of these.
    /// </summary>
    internal static CompilationUnitSyntax? EntryPointUnit(SyntaxNode root) =>
        root is CompilationUnitSyntax unit && unit.Members.OfType<GlobalStatementSyntax>().Any()
            ? unit
            : null;

    /// <summary>
    /// Every scope in one tree that owns a method body: the method declarations,
    /// plus the compilation unit when the file is a top-level program. This is
    /// the enumeration the call and callback passes walk instead of
    /// OfType&lt;BaseMethodDeclarationSyntax&gt;().
    /// </summary>
    internal static IEnumerable<SyntaxNode> MethodScopes(SyntaxNode root)
    {
        foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            yield return decl;

        if (EntryPointUnit(root) is { } unit) yield return unit;
    }

    /// <summary>
    /// The nodes forming a scope's body: the global statements for a top-level
    /// program, the declaration itself for an ordinary method. Walkers iterate
    /// this rather than descending from the scope, which is what keeps types
    /// declared beside the top-level statements out of Main's body.
    /// </summary>
    internal static IEnumerable<SyntaxNode> BodyNodes(SyntaxNode scope) =>
        scope is CompilationUnitSyntax unit
            ? unit.Members.OfType<GlobalStatementSyntax>()
            : new[] { scope };

    /// <summary>
    /// The method a scope declares: an ordinary declaration's symbol, or the
    /// synthesized entry point the compilation unit's top-level statements form.
    /// Null for a compilation unit with no global statements, which is how a
    /// caller that guessed wrong finds out.
    /// </summary>
    internal static IMethodSymbol? DeclaredMethod(SyntaxNode scope, SemanticModel model) =>
        scope is CompilationUnitSyntax unit
            ? model.GetDeclaredSymbol(unit)
            : model.GetDeclaredSymbol(scope) as IMethodSymbol;
}
