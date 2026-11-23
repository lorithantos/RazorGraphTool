namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;

/// <summary>
/// Stable node ids for symbols — the identity every declaration site and every
/// call, access, and coverage edge agrees on.
/// </summary>
internal static class SymbolIds
{
    /// <summary>
    /// Stable id for a method, shared by the declaration site and every call site.
    /// Built from the original definition so a generic instantiation
    /// (Repo&lt;string&gt;.Get) resolves to the same node as its definition, and
    /// parameter types are included so overloads stay distinct. A reduced
    /// extension-method symbol (money.Add(b) — the this parameter folded away)
    /// unreduces first: the declaration registered the static two-parameter
    /// form, and without this every call edge into an extension method was
    /// silently dropped on the id mismatch.
    /// </summary>
    public static string MethodId(IMethodSymbol method) => "m:" + MethodBody(method);

    /// <summary>
    /// Stable id for one of a method's parameters, keyed by NAME rather than
    /// ordinal: the method id already embeds the parameter type list, so any
    /// insert, delete or type change churns the whole id anyway. The one case
    /// the schemes disagree on is reordering two same-typed parameters — the
    /// method id is byte-identical, and under an ordinal key every attribute
    /// edge would silently transfer to the wrong parameter, while under the
    /// name key the ids swap correctly. Ordinal is a property, not identity.
    /// </summary>
    public static string ParameterId(IMethodSymbol method, IParameterSymbol parameter) =>
        $"param:{MethodBody(method)}#{parameter.Name}";

    /// <summary>
    /// The form of a method symbol its id is built from: unreduced, and for a
    /// C# 14 extension member, the lowered static implementation.
    /// </summary>
    /// <remarks>
    /// The two extension syntaxes have to land on the same id shape, because
    /// they are the same concept. A classic <c>this</c>-parameter method
    /// unreduces through <c>ReducedFrom</c>. A member declared inside an
    /// <c>extension</c> block has no ReducedFrom at all; its identity comes
    /// from <c>AssociatedExtensionImplementation</c>, which the compiler
    /// already shapes exactly right — declared by the containing static class,
    /// receiver first in the parameter list for an instance member and absent
    /// for a static one. So <c>Doubled()</c> in a block and
    /// <c>Doubled(this Price)</c> beside it mint the same id, and a codebase
    /// moving between the syntaxes keeps its node ids.
    /// <para>
    /// It is also what lets a call site meet its declaration: a call binds to
    /// the block's member symbol, never to the implementation, so both ends
    /// have to resolve through here or every edge into an extension member is
    /// silently dropped on the mismatch — the same failure ReducedFrom was
    /// added to fix.
    /// </para>
    /// </remarks>
    internal static IMethodSymbol Canonical(IMethodSymbol method) =>
        (method.AssociatedExtensionImplementation ?? method.ReducedFrom ?? method).OriginalDefinition;

    /// <summary>
    /// The shared body of MethodId and ParameterId — one place computes it, so
    /// the two can never drift, and a parameter's parent method id is always
    /// recoverable as "m:" + the text before the '#'.
    /// </summary>
    private static string MethodBody(IMethodSymbol method)
    {
        var def = Canonical(method);
        var container = def.ContainingType?.ToDisplayString() ?? "global";
        return $"{container}.{def.Name}({ParameterTypes(def.Parameters)})";
    }

    /// <summary>
    /// The comma-joined parameter types an overload is told apart by. Method ids
    /// and indexer ids both spell their parameters through here, so the two
    /// conventions cannot drift: <c>m:T.Get(int)</c> and <c>prop:T.this[int]</c>.
    /// </summary>
    private static string ParameterTypes(IEnumerable<IParameterSymbol> parameters) =>
        string.Join(",", parameters.Select(p => p.Type.ToDisplayString()));

    /// <summary>
    /// Stable id for a property or field node. No parameter list for a plain
    /// property or field — those cannot overload — and a distinct prefix per
    /// kind, so the id tells a reader what it names the same way m:/type:/page:
    /// already do.
    /// </summary>
    /// <remarks>
    /// An indexer is the member that CAN overload, and every indexer is named
    /// <c>this[]</c> whatever its parameters, so the name alone gave
    /// <c>this[int]</c> and <c>this[string]</c> on one type the same id: one
    /// node kept the first indexer's span, the second had none, and both
    /// getters' edges left the survivor. Every indexer carries its parameter
    /// types inside the brackets, written as MethodId writes them —
    /// <c>prop:T.this[int]</c>, <c>prop:T.this[int,string]</c> — including a
    /// type with only one, so an indexer's id does not change when an overload
    /// is added beside it. The node's name stays <c>this[]</c>.
    /// </remarks>
    /// <remarks>
    /// An extension member is the exception, and carries a parenthesised
    /// receiver: <c>prop:Money.Extensions.IsZero(Money)</c>. Two extension
    /// blocks in one static class may declare the same member name on
    /// different receiver types, which is legal C# and would otherwise mint one
    /// id for two properties and silently merge them. The receiver is written
    /// exactly as the lowered implementation's parameter list has it, so an
    /// instance member reads <c>(Money)</c> and a static one <c>()</c> — the
    /// same convention MethodId follows, and the empty parentheses are what
    /// distinguish a static extension property from a plain one of the same
    /// name on the containing class.
    /// </remarks>
    public static string MemberId(ISymbol member)
    {
        var def = member.OriginalDefinition;
        var prefix = def is IPropertySymbol ? "prop" : "field";

        if (def.ContainingType is { IsExtension: true } block)
        {
            var host = block.ContainingType?.ToDisplayString() ?? "global";
            // A static extension block declares a receiver TYPE and no
            // parameter, so its ExtensionParameter is nameless.
            var receiver = block.ExtensionParameter is { Name.Length: > 0 } p
                ? p.Type.ToDisplayString()
                : "";
            return $"{prefix}:{host}.{def.Name}({receiver})";
        }

        var container = def.ContainingType?.ToDisplayString() ?? "global";
        return $"{prefix}:{container}.{IndexerAwareName(def)}";
    }

    /// <summary>
    /// A member's name as its id spells it: an indexer's <c>this[]</c> (or an
    /// explicit implementation's <c>I.this[]</c>) with the parameter types
    /// written between the brackets, and any other member's name unchanged.
    /// </summary>
    private static string IndexerAwareName(ISymbol def) =>
        def is IPropertySymbol { IsIndexer: true } indexer && indexer.Name.EndsWith("[]", StringComparison.Ordinal)
            ? $"{indexer.Name[..^1]}{ParameterTypes(indexer.Parameters)}]"
            : def.Name;

    /// <summary>
    /// Id for a type the compilation referenced but does not contain.
    /// </summary>
    /// <remarks>
    /// ext: rather than a kind-specific prefix because the concept is wider than
    /// the first thing to need it: an attribute from xUnit today, an out-of-solution
    /// base type or interface later, all of them the same fact — a name the graph
    /// can see used and cannot open. No assembly in the id, matching the plain
    /// type: ids, which carry the identical theoretical collision and ignore it.
    ///
    /// Only for types the solution does NOT declare. One it does declare already
    /// has a node, and reusing that is what lets a single node answer both what an
    /// attribute decorates and who calls it.
    /// </remarks>
    public static string ExternalTypeId(INamedTypeSymbol type) =>
        $"ext:{type.OriginalDefinition.ToDisplayString()}";

    /// <summary>
    /// MethodId for a call target, or null when the target is declared outside
    /// the loaded projects — an edge to String.Format is noise, not navigation.
    /// </summary>
    internal static string? InScopeMethodId(IMethodSymbol target, IReadOnlySet<string> inScope)
    {
        var def = target.OriginalDefinition;
        var assembly = def.ContainingAssembly?.Name;
        if (assembly == null || !inScope.Contains(assembly)) return null;
        return MethodId(def);
    }
}
