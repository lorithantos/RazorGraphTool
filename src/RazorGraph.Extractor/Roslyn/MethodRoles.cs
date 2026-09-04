namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using RazorGraph.Extractor.Attributes;

/// <summary>
/// Recognizes the roles a framework can hand a method: test, lifecycle hook,
/// and the entry-point kinds where the runtime calls into user code. The
/// attribute name sets each predicate consults are DATA — they live in the
/// attribute policy (Attributes\attribute-policy.json), not here, so a codebase
/// with its own vocabulary changes a file rather than this code.
/// </summary>
internal static class MethodRoles
{
    /// <summary>
    /// Matched by simple name because the attribute may come from any of
    /// several assemblies and the short names do not collide with anything
    /// else in practice.
    /// </summary>
    internal static bool IsTestMethod(IMethodSymbol method, AttributePolicy policy) =>
        method.GetAttributes().Any(a =>
            a.AttributeClass != null && policy.TestAttributeNames.Contains(a.AttributeClass.Name));

    /// <summary>
    /// A hook the framework runs around each test rather than a method any test
    /// calls. Work done here is real coverage, but no Calls edge from a test will
    /// ever reach it. Only meaningful on a type that has test methods — the
    /// caller gates on that, and also flags test-class constructors, which this
    /// predicate does not see. A base-class hook is still missed: it is extracted
    /// under the base type, which has no tests of its own.
    /// </summary>
    internal static bool IsLifecycleMethod(IMethodSymbol method, AttributePolicy policy)
    {
        if (method.GetAttributes().Any(a =>
                a.AttributeClass != null && policy.LifecycleAttributeNames.Contains(a.AttributeClass.Name)))
            return true;

        return method.Name is "InitializeAsync" or "DisposeAsync" or "Dispose"
            && method.ContainingType.AllInterfaces.Any(i =>
                i.Name is "IAsyncLifetime" or "IAsyncDisposable" or "IDisposable");
    }

    private static readonly string[] PageHandlerPrefixes =
        { "OnGet", "OnPost", "OnPut", "OnDelete", "OnHead", "OnOptions", "OnPatch" };

    /// <summary>
    /// Classifies a method as an application entry point — a place the runtime
    /// or a framework calls into user code, where an escaping exception has no
    /// further user frame to stop in. First match wins; null for the ordinary
    /// methods that are the overwhelming majority. Top-level-statement Main is
    /// a declared blind spot: the synthesized method has no declaration syntax
    /// and never becomes a node. Handlers registered as lambdas are another —
    /// there is no method symbol to stamp.
    /// </summary>
    internal static string? ClassifyEntryPoint(IMethodSymbol m, IReadOnlySet<string> inScope, AttributePolicy policy)
    {
        if (m.MethodKind != MethodKind.Ordinary) return null;

        // Test methods and their hooks are framework-invoked too, but the test
        // host catches everything they throw — that is its job — so calling
        // them escape surfaces would only manufacture noise.
        if (IsTestMethod(m, policy)) return null;

        // <Main>$ is the name the compiler gives the entry point it synthesizes
        // for top-level statements. Same role as a written Main, and without it
        // the method that starts the whole application carries no entry-point
        // kind — so escape analysis has no root to report against.
        if (m.IsStatic && m.Name is "Main" or "<Main>$") return "main";

        if (!m.IsStatic && m.DeclaredAccessibility == Accessibility.Public
            && PageHandlerPrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal))
            && BaseChainHasName(m.ContainingType, "PageModel"))
            return "pageHandler";

        if (!m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && IsControllerType(m.ContainingType, policy))
            return "controllerAction";

        if (IsEventHandlerShape(m)) return "eventHandler";

        if (m.IsAsync && m.ReturnsVoid) return "asyncVoid";

        // An override of a virtual declared outside the loaded projects is a
        // framework calling in — OnStartup, OnActionExecuting, OnPaint. The
        // Object/ValueType virtuals are excluded: every record's GetHashCode
        // would qualify, drowning the real entry points in equality plumbing.
        if (m.IsOverride && m.OverriddenMethod is { } overridden
            && overridden.OriginalDefinition.ContainingType?.SpecialType
                is not (SpecialType.System_Object or SpecialType.System_ValueType)
            && overridden.OriginalDefinition.ContainingAssembly?.Name is { } assembly
            && !inScope.Contains(assembly))
            return "frameworkOverride";

        // Middleware before the general interface check so both IMiddleware
        // and convention middleware land under one filterable kind — the
        // pipeline invokes them by registration, and they are where exception
        // shaping happens.
        if (IsHttpMiddlewareShape(m)) return "middleware";

        // Implementing an interface a framework declared is the registration
        // pattern: IValueConverter, IHostedService, IExceptionHandler — the
        // framework discovers the type and calls in through the interface it
        // knows, with no source call site anywhere. The invisible-controller
        // case, generalized.
        if (ImplementsExternalInterfaceMember(m, inScope)) return "frameworkInterface";

        return null;
    }

    /// <summary>
    /// Interfaces whose implementation says nothing about frameworks calling
    /// in: the ubiquitous BCL plumbing every well-behaved type implements.
    /// SpecialType covers the core collection/disposal set; the name-based
    /// rest are the comparison/formatting family and xUnit's lifecycle hook
    /// (the test host catches what those throw).
    /// </summary>
    private static readonly HashSet<string> PlumbingInterfaceNames = new(StringComparer.Ordinal)
    {
        "IEquatable", "IComparable", "IComparer", "IEqualityComparer", "IFormattable",
        "IAsyncDisposable", "IEnumerable", "IEnumerator", "IAsyncEnumerable", "IAsyncEnumerator",
        "IAsyncLifetime"
    };

    private static bool ImplementsExternalInterfaceMember(IMethodSymbol m, IReadOnlySet<string> inScope)
    {
        if (m.IsStatic) return false;

        foreach (var iface in m.ContainingType.AllInterfaces)
        {
            if (iface.SpecialType != SpecialType.None) continue;
            if (PlumbingInterfaceNames.Contains(iface.Name)) continue;
            if (iface.ContainingAssembly?.Name is not { } assembly || inScope.Contains(assembly)) continue;

            foreach (var member in iface.GetMembers().OfType<IMethodSymbol>())
            {
                if (member.MethodKind != MethodKind.Ordinary) continue;
                if (SymbolEqualityComparer.Default.Equals(
                        m.ContainingType.FindImplementationForInterfaceMember(member), m))
                    return true;
            }
        }
        return false;
    }

    private static bool BaseChainHasName(INamedTypeSymbol? type, string name)
    {
        for (var t = type?.BaseType; t != null; t = t.BaseType)
            if (t.Name == name) return true;
        return false;
    }

    /// <summary>
    /// The controller heuristic, shared with SymbolClassifier's classification
    /// branch so the two can never disagree.
    /// </summary>
    internal static bool IsControllerType(INamedTypeSymbol type, AttributePolicy policy) =>
        (type.BaseType?.ToDisplayString() ?? "").Contains("Controller")
        || type.GetAttributes().Any(a =>
            a.AttributeClass != null && policy.ControllerAttributeNames.Contains(a.AttributeClass.Name));

    private static bool IsEventHandlerShape(IMethodSymbol m)
    {
        if (m.Parameters.Length != 2) return false;
        if (!m.ReturnsVoid && m.ReturnType.Name != "Task") return false;
        if (m.Parameters[0].Type.SpecialType != SpecialType.System_Object) return false;

        for (ITypeSymbol? t = m.Parameters[1].Type; t != null; t = t.BaseType)
            if (t.Name == "EventArgs"
                && t.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true })
                return true;
        return false;
    }

    /// <summary>
    /// ASP.NET middleware by contract or by convention: IMiddleware, or a
    /// *Middleware class with Invoke/InvokeAsync taking HttpContext first.
    /// Convention is the overwhelmingly common form — UseMiddleware finds it
    /// by reflection, another registration no call site ever witnesses.
    /// </summary>
    internal static bool IsHttpMiddlewareShape(IMethodSymbol m) =>
        m.Name is "Invoke" or "InvokeAsync"
        && !m.IsStatic
        && m.Parameters.Length >= 1
        && m.Parameters[0].Type.Name == "HttpContext"
        && (m.ContainingType.Name.EndsWith("Middleware", StringComparison.Ordinal)
            || m.ContainingType.AllInterfaces.Any(i => i.Name == "IMiddleware"));

    internal static bool IsExceptionHandlerShape(IMethodSymbol m) =>
        m.Name == "TryHandleAsync"
        && m.ContainingType.AllInterfaces.Any(i => i.Name == "IExceptionHandler");
}
