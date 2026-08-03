namespace RazorGraph.Extractor.Roslyn;

using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.MSBuild;
using RazorGraph.Core.Graph;

/// <summary>
/// Extracts C# semantic information from a compiled project using Roslyn.
/// </summary>
public sealed class RoslynExtractor : IAsyncDisposable
{
    private MSBuildWorkspace? _workspace;
    private readonly List<LoadedProject> _loaded = new();

    // Must run before any Microsoft.Build type is JITed; the ctor body is safe
    // because MSBuild types are first referenced inside LoadProjectAsync.
    public RoslynExtractor() => EnsureMsBuildRegistered();

    public static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    /// <summary>One compiled project and the Roslyn project it came from.</summary>
    public sealed record LoadedProject(Project Project, Compilation Compilation)
    {
        public string Name => Project.Name;
        public string? FilePath => Project.FilePath;
    }

    /// <summary>
    /// Every project loaded by the most recent call. One entry for a project or
    /// single-project solution load; all of them after LoadAllProjectsAsync.
    /// </summary>
    public IReadOnlyList<LoadedProject> LoadedProjects => _loaded;

    /// <summary>The solution, when one was opened. Null for a bare project load.</summary>
    public Solution? Solution { get; private set; }

    /// <summary>
    /// The compilation from the most recent load, for consumers that need
    /// symbol-level analysis (e.g., tag helper discovery).
    /// </summary>
    public Compilation? Compilation => _loaded.Count > 0 ? _loaded[0].Compilation : null;

    /// <summary>File path of the loaded project, for locating its Razor files.</summary>
    public string? ProjectFilePath => _loaded.Count > 0 ? _loaded[0].FilePath : null;

    public async Task<Compilation> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        var project = await _workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectPath}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        return compilation;
    }

    public async Task<Compilation> LoadSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        var project = Solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found in solution.");
        var compilation = await project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectName}");

        _loaded.Clear();
        _loaded.Add(new LoadedProject(project, compilation));
        return compilation;
    }

    /// <summary>
    /// Load every project in the solution. This is what makes a cross-project
    /// edge possible at all: call resolution is scoped to the assemblies that
    /// were compiled, so a graph built one project at a time can never contain
    /// an edge from a test to the code it tests.
    /// </summary>
    public async Task<IReadOnlyList<LoadedProject>> LoadAllProjectsAsync(string solutionPath, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        Solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);

        _loaded.Clear();
        foreach (var project in Solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            // A project that will not compile is reported and skipped rather than
            // failing the whole solution: a partial graph beats no graph, and the
            // omission is visible in the project list.
            var compilation = await project.GetCompilationAsync(ct);
            if (compilation == null)
            {
                Console.Error.WriteLine($"Warning: no compilation for project '{project.Name}'; skipped.");
                continue;
            }

            _loaded.Add(new LoadedProject(project, compilation));
        }

        if (_loaded.Count == 0)
            throw new InvalidOperationException($"No project in '{solutionPath}' produced a compilation.");

        return _loaded;
    }

    /// <summary>
    /// Extract all relevant symbols: PageModels, Controllers, Services, ViewModels.
    /// </summary>
    public IEnumerable<SymbolInfo> ExtractSymbols()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var loaded in _loaded)
        {
            foreach (var tree in loaded.Compilation.SyntaxTrees)
            {
                var model = loaded.Compilation.GetSemanticModel(tree);
                var root = tree.GetRoot();

                foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var symbol = model.GetDeclaredSymbol(typeDecl);
                    if (symbol == null) continue;

                    var info = ClassifySymbol(symbol, loaded.Name);
                    if (info != null) yield return info;
                }
            }
        }
    }

    private SymbolInfo? ClassifySymbol(INamedTypeSymbol symbol, string projectName)
    {
        var baseType = symbol.BaseType?.ToDisplayString() ?? "";
        var interfaces = symbol.AllInterfaces.Select(i => i.ToDisplayString()).ToList();
        var (lineStart, lineEnd) = GetLines(symbol);

        // PageModel detection
        if (baseType.Contains("PageModel") || baseType.Contains("Microsoft.AspNetCore.Mvc.RazorPages.PageModel"))
        {
            return new SymbolInfo
            {
                Id = $"pm:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.PageModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Properties = ExtractProperties(symbol),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Controller detection
        if (baseType.Contains("Controller") || symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute"))
        {
            return new SymbolInfo
            {
                Id = $"ctrl:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.ApiController,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Methods = ExtractControllerActions(symbol),
                MethodNodes = ExtractMethodNodes(symbol),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Service detection (heuristic: ends with Service, or implements interface ending with Service)
        if (symbol.Name.EndsWith("Service") || interfaces.Any(i => i.EndsWith("Service")))
        {
            return new SymbolInfo
            {
                Id = $"svc:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = symbol.TypeKind == TypeKind.Interface ? NodeType.ServiceInterface : NodeType.ServiceImplementation,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                ImplementedInterfaces = interfaces.Where(i => i.EndsWith("Service")).ToList(),
                Methods = ExtractMethods(symbol),
                MethodNodes = ExtractMethodNodes(symbol)
            };
        }

        // ViewModel detection (heuristic: ends with VM or ViewModel, or used in @model directives)
        if (symbol.Name.EndsWith("VM") || symbol.Name.EndsWith("ViewModel") || symbol.Name.EndsWith("Model"))
        {
            return new SymbolInfo
            {
                Id = $"vm:{symbol.ToDisplayString()}",
                Project = projectName,
                Type = NodeType.ViewModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                Properties = ExtractProperties(symbol),
                MethodNodes = ExtractMethodNodes(symbol)
            };
        }

        // Everything else that is still a declared type in this project. Without
        // this the graph silently omits most of the codebase -- helpers, domain
        // types, extension classes -- and "who calls this" cannot be answered
        // because the caller was never a node.
        if (IsCompilerGenerated(symbol)) return null;

        return new SymbolInfo
        {
            Id = $"type:{symbol.ToDisplayString()}",
            Project = projectName,
            Type = NodeType.Class,
            Name = symbol.Name,
            FullName = symbol.ToDisplayString(),
            FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
            LineStart = lineStart,
            LineEnd = lineEnd,
            BaseType = baseType,
            ImplementedInterfaces = interfaces,
            Properties = ExtractProperties(symbol),
            Methods = ExtractMethods(symbol),
            MethodNodes = ExtractMethodNodes(symbol),
            InjectedServices = ExtractInjectedServices(symbol)
        };
    }

    // Names the compiler mints for itself (<>c__DisplayClass, record equality
    // helpers) are never source the user can navigate to.
    private static bool IsCompilerGenerated(INamedTypeSymbol symbol) =>
        symbol.IsImplicitlyDeclared || symbol.Name.StartsWith('<') || symbol.Name.Length == 0;

    private static (int? Start, int? End) GetLines(INamedTypeSymbol symbol)
    {
        var syntaxRef = symbol.DeclaringSyntaxReferences.FirstOrDefault();
        if (syntaxRef == null) return (null, null);
        var span = syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span);
        return (span.StartLinePosition.Line + 1, span.EndLinePosition.Line + 1);
    }

    private List<PropertyInfo> ExtractProperties(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IPropertySymbol>()
            .Select(p => new PropertyInfo
            {
                Name = p.Name,
                Type = p.Type.ToDisplayString(),
                IsPublic = p.DeclaredAccessibility == Accessibility.Public,
                HasBindProperty = p.GetAttributes().Any(a => a.AttributeClass?.Name == "BindPropertyAttribute")
            })
            .ToList();

    private List<MethodInfo> ExtractMethods(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && m.DeclaredAccessibility == Accessibility.Public)
            .Select(m => new MethodInfo
            {
                Name = m.Name,
                ReturnType = m.ReturnType.ToDisplayString(),
                Parameters = m.Parameters.Select(p => p.Type.ToDisplayString()).ToList(),
                IsAsync = m.IsAsync,
                HttpMethod = InferHttpMethod(m)
            })
            .ToList();

    private List<MethodInfo> ExtractControllerActions(INamedTypeSymbol symbol) =>
        symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.DeclaredAccessibility == Accessibility.Public && !m.IsStatic)
            .Select(m => new MethodInfo
            {
                Name = m.Name,
                ReturnType = m.ReturnType.ToDisplayString(),
                Parameters = m.Parameters.Select(p => $"{p.Type.ToDisplayString()} {p.Name}").ToList(),
                IsAsync = m.IsAsync,
                HttpMethod = InferHttpMethod(m),
                Route = InferRoute(m)
            })
            .ToList();

    /// <summary>
    /// Every ordinary method on the type, at any accessibility, as a candidate
    /// graph node. Distinct from <see cref="ExtractMethods"/>, which describes the
    /// type's public surface: a call graph that omitted private methods would
    /// break every chain that passes through a helper.
    /// </summary>
    private List<MethodDetail> ExtractMethodNodes(INamedTypeSymbol symbol)
    {
        var members = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Ordinary && !m.IsImplicitlyDeclared)
            .ToList();

        // Lifecycle hooks only count as such on a type that actually has tests;
        // otherwise every IDisposable.Dispose in production code would be flagged.
        var hasTests = members.Any(IsTestMethod);

        return members
            .Select(m =>
            {
                var syntaxRef = m.DeclaringSyntaxReferences.FirstOrDefault();
                int? line = syntaxRef == null
                    ? null
                    : syntaxRef.SyntaxTree.GetLineSpan(syntaxRef.Span).StartLinePosition.Line + 1;

                return new MethodDetail
                {
                    Id = MethodId(m),
                    Name = m.Name,
                    Signature = $"{m.Name}({string.Join(", ", m.Parameters.Select(p => p.Type.ToDisplayString()))})",
                    ReturnType = m.ReturnType.ToDisplayString(),
                    IsAsync = m.IsAsync,
                    IsPublic = m.DeclaredAccessibility == Accessibility.Public,
                    IsStatic = m.IsStatic,
                    IsTest = IsTestMethod(m),
                    IsTestLifecycle = hasTests && IsLifecycleMethod(m),
                    // Interface members and abstract methods have no body. They are
                    // still nodes worth having (calls bind to them), but they are not
                    // code that a test could execute.
                    IsAbstract = m.IsAbstract,
                    FilePath = m.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                    LineStart = line
                };
            })
            .ToList();
    }

    /// <summary>
    /// Attribute names that mark a method as a test across the three frameworks
    /// in common use. Matched by simple name because the attribute may come from
    /// any of several assemblies and the short names do not collide with
    /// anything else in practice.
    /// </summary>
    private static readonly HashSet<string> TestAttributeNames = new(StringComparer.Ordinal)
    {
        "FactAttribute", "TheoryAttribute",                        // xUnit
        "TestAttribute", "TestCaseAttribute", "TestCaseSourceAttribute", // NUnit
        "TestMethodAttribute", "DataTestMethodAttribute"           // MSTest
    };

    private static bool IsTestMethod(IMethodSymbol method) =>
        method.GetAttributes().Any(a =>
            a.AttributeClass != null && TestAttributeNames.Contains(a.AttributeClass.Name));

    /// <summary>
    /// Setup/teardown attribute names for the frameworks that mark hooks with
    /// attributes. xUnit is absent by design: its hooks are interface members.
    /// </summary>
    private static readonly HashSet<string> LifecycleAttributeNames = new(StringComparer.Ordinal)
    {
        "SetUpAttribute", "TearDownAttribute",
        "OneTimeSetUpAttribute", "OneTimeTearDownAttribute",       // NUnit
        "TestInitializeAttribute", "TestCleanupAttribute",
        "ClassInitializeAttribute", "ClassCleanupAttribute"        // MSTest
    };

    /// <summary>
    /// A hook the framework runs around each test rather than a method any test
    /// calls. Work done here is real coverage, but no Calls edge from a test will
    /// ever reach it. Only meaningful on a type that has test methods — the
    /// caller gates on that. Constructor-based setup is not represented, because
    /// constructors are not graph nodes; a base-class hook is likewise missed,
    /// since it is extracted under the base type, which has no tests of its own.
    /// </summary>
    private static bool IsLifecycleMethod(IMethodSymbol method)
    {
        if (method.GetAttributes().Any(a =>
                a.AttributeClass != null && LifecycleAttributeNames.Contains(a.AttributeClass.Name)))
            return true;

        return method.Name is "InitializeAsync" or "DisposeAsync" or "Dispose"
            && method.ContainingType.AllInterfaces.Any(i =>
                i.Name is "IAsyncLifetime" or "IAsyncDisposable" or "IDisposable");
    }

    /// <summary>
    /// Stable id for a method, shared by the declaration site and every call site.
    /// Built from the original definition so a generic instantiation
    /// (Repo&lt;string&gt;.Get) resolves to the same node as its definition, and
    /// parameter types are included so overloads stay distinct.
    /// </summary>
    public static string MethodId(IMethodSymbol method)
    {
        var def = method.OriginalDefinition;
        var parameters = string.Join(",", def.Parameters.Select(p => p.Type.ToDisplayString()));
        var container = def.ContainingType?.ToDisplayString() ?? "global";
        return $"m:{container}.{def.Name}({parameters})";
    }

    /// <summary>
    /// Resolve call sites to (caller, callee) method-id pairs. Only calls whose
    /// target is declared in one of the loaded projects are returned -- an edge to
    /// String.Format would be noise, not navigation.
    ///
    /// Membership is tested by assembly *name*, not symbol identity. A call into
    /// a sibling project may bind to either a source symbol or a metadata symbol
    /// depending on how the workspace resolved the reference, and those are not
    /// reference-equal; the name is the same either way, and so is the MethodId
    /// the node was registered under.
    ///
    /// Besides explicit invocations, a using/await using counts as a call to the
    /// resource's Dispose/DisposeAsync: the compiler emits that call with no
    /// invocation syntax at the site, and without the edge a dispose method
    /// reads as unreached by the very code that guarantees it runs.
    /// </summary>
    public IEnumerable<(string FromId, string ToId)> ExtractCallEdges()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = _loaded
            .Select(l => l.Compilation.Assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var loaded in _loaded)
        {
            foreach (var tree in loaded.Compilation.SyntaxTrees)
            {
                var model = loaded.Compilation.GetSemanticModel(tree);

                foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    if (model.GetDeclaredSymbol(decl) is not IMethodSymbol caller) continue;
                    var fromId = MethodId(caller);

                    foreach (var invocation in decl.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    {
                        var symbolInfo = model.GetSymbolInfo(invocation);
                        // CandidateSymbols covers calls the compiler could not fully bind
                        // (an overload set narrowed by a dynamic or erroneous argument).
                        var target = symbolInfo.Symbol as IMethodSymbol
                            ?? symbolInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                        if (target == null) continue;

                        var def = target.OriginalDefinition;
                        var assembly = def.ContainingAssembly?.Name;
                        if (assembly == null || !inScope.Contains(assembly)) continue;

                        var toId = MethodId(def);
                        if (toId == fromId) continue; // direct recursion adds no navigational value

                        yield return (fromId, toId);
                    }

                    foreach (var (resourceType, isAsync) in DisposedResources(decl, model))
                    {
                        if (ResolveDisposeMethod(resourceType, isAsync) is not { } dispose) continue;

                        var def = dispose.OriginalDefinition;
                        var assembly = def.ContainingAssembly?.Name;
                        if (assembly == null || !inScope.Contains(assembly)) continue;

                        var toId = MethodId(def);
                        if (toId == fromId) continue;

                        yield return (fromId, toId);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Every resource a method disposes implicitly: using statements (block and
    /// expression forms) and using declarations, with await variants marked so
    /// the caller resolves DisposeAsync rather than Dispose.
    /// </summary>
    private static IEnumerable<(ITypeSymbol Type, bool IsAsync)> DisposedResources(
        BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        foreach (var node in decl.DescendantNodes())
        {
            switch (node)
            {
                case UsingStatementSyntax u:
                    var isAsync = u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);
                    if (u.Declaration != null)
                    {
                        foreach (var variable in u.Declaration.Variables)
                            if (model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                                yield return (local.Type, isAsync);
                    }
                    else if (u.Expression != null && model.GetTypeInfo(u.Expression).Type is { } expressionType)
                    {
                        yield return (expressionType, isAsync);
                    }
                    break;

                case LocalDeclarationStatementSyntax l when l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                    foreach (var variable in l.Declaration.Variables)
                        if (model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                            yield return (local.Type, l.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword));
                    break;
            }
        }
    }

    /// <summary>
    /// The Dispose/DisposeAsync a using construct actually runs: the interface
    /// implementation when the type is IDisposable/IAsyncDisposable, otherwise a
    /// parameterless method found by shape — ref structs and pattern-based
    /// disposal bind by name, not interface.
    /// </summary>
    private static IMethodSymbol? ResolveDisposeMethod(ITypeSymbol type, bool isAsync)
    {
        var interfaceName = isAsync ? "IAsyncDisposable" : "IDisposable";
        var methodName = isAsync ? "DisposeAsync" : "Dispose";

        var interfaceMember = type.AllInterfaces
            .FirstOrDefault(i => i.Name == interfaceName && i.ContainingNamespace is { Name: "System", ContainingNamespace.IsGlobalNamespace: true })
            ?.GetMembers(methodName).OfType<IMethodSymbol>().FirstOrDefault();
        if (interfaceMember != null && type.FindImplementationForInterfaceMember(interfaceMember) is IMethodSymbol implementation)
            return implementation;

        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            var byShape = t.GetMembers(methodName).OfType<IMethodSymbol>()
                .FirstOrDefault(m => !m.IsStatic && m.Parameters.Length == 0);
            if (byShape != null) return byShape;
        }
        return null;
    }

    private List<string> ExtractInjectedServices(INamedTypeSymbol symbol)
    {
        var ctor = symbol.InstanceConstructors.FirstOrDefault(c => c.DeclaredAccessibility == Accessibility.Public);
        if (ctor == null) return new List<string>();
        return ctor.Parameters.Select(p => p.Type.ToDisplayString()).ToList();
    }

    private static string? InferHttpMethod(IMethodSymbol method)
    {
        var attrs = method.GetAttributes();
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpGetAttribute")) return "GET";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPostAttribute")) return "POST";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPutAttribute")) return "PUT";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpDeleteAttribute")) return "DELETE";
        if (attrs.Any(a => a.AttributeClass?.Name == "HttpPatchAttribute")) return "PATCH";
        return null;
    }

    private static string? InferRoute(IMethodSymbol method)
    {
        var routeAttr = method.GetAttributes()
            .FirstOrDefault(a => a.AttributeClass?.Name is "RouteAttribute" or "HttpGetAttribute" or "HttpPostAttribute");
        if (routeAttr == null) return null;
        return routeAttr.ConstructorArguments.FirstOrDefault().Value?.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        if (_workspace != null)
        {
            _workspace.CloseSolution();
            _workspace.Dispose();
        }
    }
}

public sealed class SymbolInfo
{
    public required string Id { get; init; }
    public required NodeType Type { get; init; }
    public required string Name { get; init; }
    public required string FullName { get; init; }

    /// <summary>Name of the project whose compilation declared this type.</summary>
    public string? Project { get; init; }

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }
    public string? BaseType { get; init; }
    public List<string> ImplementedInterfaces { get; init; } = new();
    public List<string> InjectedServices { get; init; } = new();
    public List<PropertyInfo> Properties { get; init; } = new();
    public List<MethodInfo> Methods { get; init; } = new();

    /// <summary>Members promoted to their own graph nodes; see ExtractMethodNodes.</summary>
    public List<MethodDetail> MethodNodes { get; init; } = new();
}

/// <summary>
/// A method as a graph node: identity, location, and the shape a reader needs to
/// decide whether to open the file.
/// </summary>
public sealed class MethodDetail
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Signature { get; init; }
    public string ReturnType { get; init; } = string.Empty;
    public bool IsAsync { get; init; }
    public bool IsPublic { get; init; }
    public bool IsStatic { get; init; }

    /// <summary>Carries a [Fact]/[Test]/[TestMethod]-style attribute.</summary>
    public bool IsTest { get; init; }

    /// <summary>
    /// A setup/teardown hook on a test class — [SetUp]-style attribute or an
    /// xUnit IAsyncLifetime/IDisposable member. The framework calls it, no test
    /// does, so coverage traversal must seed from it alongside the tests.
    /// </summary>
    public bool IsTestLifecycle { get; init; }

    /// <summary>Declared without a body — an interface member or an abstract method.</summary>
    public bool IsAbstract { get; init; }

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
}

public sealed class PropertyInfo
{
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public bool IsPublic { get; set; }
    public bool HasBindProperty { get; set; }
}

public sealed class MethodInfo
{
    public string Name { get; set; } = string.Empty;
    public string ReturnType { get; set; } = string.Empty;
    public List<string> Parameters { get; set; } = new();
    public bool IsAsync { get; set; }
    public string? HttpMethod { get; set; }
    public string? Route { get; set; }
}
