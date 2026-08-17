namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;

/// <summary>
/// Stable node ids for symbols — the identity every declaration site and every
/// call, access, and coverage edge agrees on.
/// </summary>
public static class SymbolIds
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
    public static string MethodId(IMethodSymbol method)
    {
        var def = (method.ReducedFrom ?? method).OriginalDefinition;
        var parameters = string.Join(",", def.Parameters.Select(p => p.Type.ToDisplayString()));
        var container = def.ContainingType?.ToDisplayString() ?? "global";
        return $"m:{container}.{def.Name}({parameters})";
    }

    /// <summary>
    /// Stable id for a property or field node. No parameter list — members
    /// cannot overload — and a distinct prefix per kind, so the id tells a
    /// reader what it names the same way m:/type:/page: already do.
    /// </summary>
    public static string MemberId(ISymbol member)
    {
        var def = member.OriginalDefinition;
        var prefix = def is IPropertySymbol ? "prop" : "field";
        var container = def.ContainingType?.ToDisplayString() ?? "global";
        return $"{prefix}:{container}.{def.Name}";
    }

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
