namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Finds every place code names something with a string rather than by
/// referencing it -- the raw material for Quotes edges.
///
/// This scanner deliberately does NOT decide what is interesting. It yields
/// every string a member produces, and the emitter keeps only the ones whose
/// value matches a declared name somewhere in the solution. Filtering here
/// would need the solution-wide name index, which does not exist yet while a
/// single tree is being walked.
/// </summary>
internal static class QuotedNameScanner
{
    /// <summary>
    /// Names below this length carry no signal. "Id" and "To" match declarations
    /// in every codebase and would bury the real findings, which is the failure
    /// that made the first visibility report unusable.
    /// </summary>
    private const int ShortestUsefulName = 3;

    /// <summary>
    /// Every quoted name in one tree, attributed to the code that produces it.
    ///
    /// Four provenances, and the distinction is the point rather than
    /// bookkeeping: nameof survives a rename and a typed literal does not, so
    /// the same coupling is a fact in one case and a defect in the other. An IL
    /// reader cannot tell them apart -- they compile to identical instructions --
    /// which is exactly why this is done over syntax.
    /// </summary>
    internal static IEnumerable<QuotedName> TreeQuotedNames(SyntaxNode root, SemanticModel model)
    {
        foreach (var node in root.DescendantNodes())
        {
            (string? Value, string? Provenance) found = node switch
            {
                // nameof(X.Member) folds to a constant, so the value is the
                // compiler's rather than the source's.
                InvocationExpressionSyntax inv when IsNameOf(inv) =>
                    (model.GetConstantValue(inv).Value as string, "nameof"),

                LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression) =>
                    (lit.Token.ValueText, InsideAttribute(lit) ? "attributeArgument" : "literal"),

                // The literal halves of an interpolated string. A binding path
                // assembled from pieces still names its target.
                InterpolatedStringTextSyntax text =>
                    (text.TextToken.ValueText, "interpolated"),

                _ => (null, null)
            };

            if (found.Value is not { } value || found.Provenance is not { } provenance) continue;
            if (value.Length < ShortestUsefulName) continue;

            var line = node.GetLocation().GetMappedLineSpan().StartLinePosition.Line + 1;
            foreach (var fromId in SyntaxAttribution.OwningNodeIds(node, model))
                yield return new QuotedName(fromId, value, provenance, line);
        }
    }

    private static bool IsNameOf(InvocationExpressionSyntax invocation) =>
        invocation.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" }
        && invocation.ArgumentList.Arguments.Count == 1;

    /// <summary>
    /// An attribute argument is a string the FRAMEWORK reads, not one this code
    /// reads, so it fails differently: nothing in the assembly breaks, and the
    /// binding silently stops happening.
    /// </summary>
    private static bool InsideAttribute(SyntaxNode node) =>
        node.FirstAncestorOrSelf<AttributeSyntax>() != null;
}
