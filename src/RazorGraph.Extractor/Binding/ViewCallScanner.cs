namespace RazorGraph.Extractor.Binding;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using RazorGraph.Extractor.Roslyn;

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
    string MethodId,
    string? Name,
    string Controller,
    ViewNameSource Source,
    int Line,
    string? Reason = null);

/// <summary>
/// One place code names an OrchardCore shape.
/// </summary>
/// <param name="IsAlternate">
/// True when the name was added to Metadata.Alternates rather than naming the
/// shape itself. Alternates are the override mechanism, so they are the most
/// specific bindings rather than an afterthought.
/// </param>
public sealed record ShapeReference(string MethodId, string Name, bool IsAlternate, int Line);

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
    /// Calls that name an OrchardCore shape. Unlike an MVC view name there is no
    /// implicit form: a shape is always named, so a non-constant argument is the
    /// only unknown case.
    /// </summary>
    private static readonly HashSet<string> ShapeMethods =
        new(StringComparer.Ordinal) { "Initialize", "View", "Shape", "CreateAsync", "New" };

    /// <summary>
    /// Shape names produced anywhere in a type — display drivers, shape factory
    /// calls, alternates added to metadata.
    ///
    /// Separate from <see cref="ViewCalls"/> because a display driver is not a
    /// controller: it has no action name to fall back on, and its View("X") means
    /// "the shape called X", not "the view file X" in a controller's folder.
    /// </summary>
    public static IEnumerable<ShapeReference> ShapeNames(BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(decl) is not IMethodSymbol producer) yield break;
        if (!IsShapeProducer(producer.ContainingType)) yield break;

        var methodId = SymbolIds.MethodId(producer);

        foreach (var invocation in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (MethodNameOf(invocation.Expression) is not { } called) continue;

            var isAlternate = called is "Add" && invocation.ToString().Contains("Alternates", StringComparison.Ordinal);
            if (!isAlternate && !ShapeMethods.Contains(called)) continue;

            var argument = invocation.ArgumentList.Arguments.FirstOrDefault()?.Expression;
            if (argument is null) continue;

            var constant = model.GetConstantValue(argument);
            if (constant is not { HasValue: true, Value: string name } || name.Length == 0) continue;

            // Shape names are identifiers with underscores; a sentence or a path
            // is some other string argument that happens to sit in first position.
            if (!IsShapeName(name)) continue;

            yield return new ShapeReference(
                methodId, name, isAlternate,
                invocation.GetLocation().GetLineSpan().StartLinePosition.Line + 1);
        }
    }

    /// <summary>
    /// A type that produces shapes: a display driver, or anything holding a shape
    /// factory. Checked so an ordinary Initialize or View call elsewhere in a
    /// solution is not read as naming a shape.
    /// </summary>
    private static bool IsShapeProducer(INamedTypeSymbol? type)
    {
        if (type is null) return false;
        if (type.Name.Contains("DisplayDriver", StringComparison.Ordinal)) return true;

        for (var b = type.BaseType; b is not null; b = b.BaseType)
        {
            if (b.Name.Contains("DisplayDriver", StringComparison.Ordinal)
                || b.Name.Contains("ShapeTableProvider", StringComparison.Ordinal)) return true;
        }

        return type.AllInterfaces.Any(i =>
            i.Name.Contains("DisplayDriver", StringComparison.Ordinal)
            || i.Name.Contains("ShapeTableProvider", StringComparison.Ordinal));
    }

    /// <summary>Shape names look like identifiers, with underscores as separators.</summary>
    private static bool IsShapeName(string name) =>
        name.Length > 0
        && char.IsLetter(name[0])
        && name.All(c => char.IsLetterOrDigit(c) || c == '_');

    /// <summary>
    /// Views rendered by one method declaration. Empty for a method that renders
    /// none, which is most of them.
    /// </summary>
    public static IEnumerable<ViewCall> ViewCalls(BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        if (model.GetDeclaredSymbol(decl) is not IMethodSymbol action) yield break;

        var methodId = SymbolIds.MethodId(action);
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
                yield return new ViewCall(methodId, null, controller, ViewNameSource.Dynamic, line,
                    "overload could not be resolved");
                continue;
            }

            var nameArgument = NameArgument(invocation, target);
            if (nameArgument is null)
            {
                // No name parameter bound: ASP.NET falls back to the action name.
                // Which action, though, comes from route data at runtime — the
                // action that was INVOKED, not the method doing the rendering. A
                // private helper called by two actions renders two different
                // views, so its own name is not the answer and claiming it would
                // report a missing template that never existed.
                if (!IsAction(action))
                {
                    yield return new ViewCall(methodId, null, controller, ViewNameSource.Dynamic, line,
                        $"rendered from non-action '{action.Name}'; the view name is the invoking action's, taken from route data");
                    continue;
                }

                // The action name is NOT always the method name.
                yield return new ViewCall(methodId, ActionNameOf(action), controller, ViewNameSource.ActionName, line);
                continue;
            }

            // Constant folding, not literal matching, so a const field or a
            // nameof() counts too.
            var constant = model.GetConstantValue(nameArgument);
            if (constant is { HasValue: true, Value: string name } && name.Length > 0)
            {
                yield return new ViewCall(methodId, name, controller, ViewNameSource.Constant, line);
                continue;
            }

            yield return new ViewCall(methodId, null, controller, ViewNameSource.Dynamic, line,
                $"view name is not a compile-time constant: {nameArgument}");
        }
    }

    /// <summary>
    /// Whether this method is reachable as an action. Only public instance
    /// methods are; a private helper returning a view result is rendering on
    /// behalf of whichever action called it.
    /// </summary>
    private static bool IsAction(IMethodSymbol method) =>
        method.DeclaredAccessibility == Accessibility.Public
        && !method.IsStatic
        && method.MethodKind == MethodKind.Ordinary
        && !method.GetAttributes().Any(a => a.AttributeClass?.Name is "NonActionAttribute" or "NonAction");

    /// <summary>
    /// The action's name for routing and view lookup, which is the method name
    /// only until <c>[ActionName]</c> says otherwise.
    ///
    /// The POST half of a form is conventionally a separate method with a
    /// distinct C# name and an attribute putting it back under the original
    /// action — <c>[ActionName(nameof(Login))] LoginPOST(...)</c> — and it renders
    /// Login.cshtml. Taking the method name there invents LoginPOST.cshtml, a
    /// template that exists nowhere: 11 of OrchardCore.Users' 53 view calls were
    /// reported as missing templates for exactly this reason before the attribute
    /// was honoured.
    /// </summary>
    private static string ActionNameOf(IMethodSymbol action)
    {
        foreach (var attribute in action.GetAttributes())
        {
            if (attribute.AttributeClass?.Name is not ("ActionNameAttribute" or "ActionName")) continue;
            if (attribute.ConstructorArguments.Length == 0) continue;

            // nameof(Login) folds to a constant the same as a literal would.
            if (attribute.ConstructorArguments[0].Value is string name && name.Length > 0) return name;
        }

        return action.Name;
    }

    /// <summary>
    /// The expression bound to a <c>string viewName</c> parameter, or null when the
    /// overload takes no name.
    /// </summary>
    private static ExpressionSyntax? NameArgument(InvocationExpressionSyntax invocation, IMethodSymbol target)
    {
        var index = -1;
        for (var i = 0; i < target.Parameters.Length; i++)
        {
            if (target.Parameters[i].Type.SpecialType != SpecialType.System_String) continue;
            index = i;
            break;
        }
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
