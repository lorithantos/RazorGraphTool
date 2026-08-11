namespace RazorGraph.Extractor.Binding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// One view a controller action renders.
/// </summary>
/// <param name="Name">
/// The view name, or null when it could not be determined statically.
/// </param>
/// <param name="Controller">
/// Controller name without the suffix — the folder ASP.NET searches first, and
/// the only thing separating the 34 files called Edit in OrchardCore.
/// </param>
/// <param name="Source">How the name was arrived at, kept so a reader can weigh it.</param>
/// <param name="Reason">Why the name is unknown, when it is.</param>
public sealed record ViewCall(
    string? Name,
    string Controller,
    ViewNameSource Source,
    int Line,
    string? Reason = null);

/// <summary>Where a view name came from.</summary>
public enum ViewNameSource
{
    /// <summary>No name argument: ASP.NET uses the action's own name.</summary>
    ActionName,

    /// <summary>A name argument that folded to a compile-time constant.</summary>
    Constant,

    /// <summary>A name argument that did not fold. Not resolvable, and reported as such.</summary>
    Dynamic
}

/// <summary>
/// Finds the views a controller action renders, and works out the name.
///
/// The name is usually not written down. Measured across OrchardCore, 253 of its
/// 346 `return View(...)` calls pass a bare identifier, 28 pass nothing, and only
/// 64 pass a literal — but that does NOT make them dynamic. `View(model)` renders
/// the action's own name, which is known at compile time; implicit and dynamic
/// are different things.
///
/// Telling `View(model)` from `View(viewName)` is impossible from the syntax —
/// both are one identifier — so this asks the semantic model which overload was
/// selected rather than inspecting argument text. That is the difference between
/// resolving almost every call and resolving only the 64 literals.
/// </summary>
public static class ViewCallScanner
{
    private static readonly HashSet<string> RenderMethods =
        new(StringComparer.Ordinal) { "View", "PartialView" };

    /// <summary>
    /// Views rendered by one method declaration. Empty for a method that renders
    /// none, which is most of them.
    /// </summary>
    public static IEnumerable<ViewCall> ViewCalls(BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(decl) is not IMethodSymbol action) yield break;

        var controller = ControllerNameOf(action.ContainingType);
        if (controller is null) yield break;

        foreach (var invocation in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MethodNameOf(invocation.Expression) is not { } called) continue;
            if (!RenderMethods.Contains(called)) continue;

            var line = invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1;

            // Which overload ran decides everything. A failed resolution is left
            // dynamic rather than guessed at: assuming the argument is a name
            // would turn every View(model) call into a view called "model".
            if (model.GetSymbolInfo(invocation).Symbol is not IMethodSymbol target)
            {
                yield return new ViewCall(null, controller, ViewNameSource.Dynamic, line,
                    "overload could not be resolved");
                continue;
            }

            var nameArgument = NameArgument(invocation, target);
            if (nameArgument is null)
            {
                // No name parameter bound: ASP.NET falls back to the action name.
                yield return new ViewCall(action.Name, controller, ViewNameSource.ActionName, line);
                continue;
            }

            // Constant folding, not literal matching, so a const field or a
            // nameof() counts too.
            var constant = model.GetConstantValue(nameArgument);
            if (constant is { HasValue: true, Value: string name } && name.Length > 0)
            {
                yield return new ViewCall(name, controller, ViewNameSource.Constant, line);
                continue;
            }

            yield return new ViewCall(null, controller, ViewNameSource.Dynamic, line,
                $"view name is not a compile-time constant: {nameArgument}");
        }
    }

    /// <summary>
    /// The expression bound to a <c>string viewName</c> parameter, or null when the
    /// overload takes no name.
    /// </summary>
    private static ExpressionSyntax? NameArgument(InvocationExpressionSyntax invocation, IMethodSymbol target)
    {
        var index = target.Parameters.IndexOf(
            target.Parameters.FirstOrDefault(p => p.Type.SpecialType == SpecialType.System_String));
        if (index < 0) return null;

        var arguments = invocation.ArgumentList.Arguments;

        // A named argument can appear anywhere, so honour it before position.
        foreach (var argument in arguments)
        {
            if (argument.NameColon?.Name.Identifier.ValueText == target.Parameters[index].Name)
                return argument.Expression;
        }

        return index < arguments.Count && arguments[index].NameColon is null
            ? arguments[index].Expression
            : null;
    }

    /// <summary>
    /// The controller's folder name, or null when the type is not a controller.
    /// Matches SymbolClassifier's rule so both agree on what a controller is.
    /// </summary>
    private static string? ControllerNameOf(INamedTypeSymbol? type)
    {
        if (type is null) return null;

        var looksLikeController = type.Name.EndsWith("Controller", StringComparison.Ordinal);
        for (var b = type.BaseType; b is not null && !looksLikeController; b = b.BaseType)
        {
            looksLikeController = b.Name.Contains("Controller", StringComparison.Ordinal);
        }
        if (!looksLikeController) return null;

        return type.Name.EndsWith("Controller", StringComparison.Ordinal)
            ? type.Name[..^"Controller".Length]
            : type.Name;
    }

    private static string? MethodNameOf(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.ValueText,
        MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
        _ => null
    };
}
