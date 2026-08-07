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

        var inScope = _loaded
            .Select(l => l.Compilation.Assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (loaded, tree) in _loaded.SelectMany(l => l.Compilation.SyntaxTrees.Select(t => (l, t))))
        {
            var model = loaded.Compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var info = ClassifySymbol(symbol, loaded.Name, loaded.Compilation, inScope);
                if (info != null) yield return info;
            }
        }
    }

    private SymbolInfo? ClassifySymbol(
        INamedTypeSymbol symbol, string projectName, Compilation compilation, IReadOnlySet<string> inScope)
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
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
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
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
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
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope)
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
                MethodNodes = ExtractMethodNodes(symbol, compilation, inScope)
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
            MethodNodes = ExtractMethodNodes(symbol, compilation, inScope),
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
    /// Every ordinary method and explicit instance constructor on the type, at
    /// any accessibility, as a candidate graph node. Distinct from
    /// <see cref="ExtractMethods"/>, which describes the type's public surface: a
    /// call graph that omitted private methods would break every chain that
    /// passes through a helper.
    ///
    /// Constructors run real code (xUnit's primary setup idiom is the test-class
    /// ctor), so leaving them out made everything reached only through one
    /// invisible to coverage. An implicit default ctor is included only when the
    /// type has instance field/property initializers — then it is exactly the
    /// code that runs them; otherwise it runs nothing and would only ever read
    /// as uncovered noise. Static ctors stay out for the same reason: no
    /// syntactic call site can ever reach one.
    /// </summary>
    private List<MethodDetail> ExtractMethodNodes(
        INamedTypeSymbol symbol, Compilation compilation, IReadOnlySet<string> inScope)
    {
        var hasInitializers = HasInstanceInitializers(symbol);

        var members = symbol.GetMembers().OfType<IMethodSymbol>()
            .Where(m => m.MethodKind switch
            {
                MethodKind.Ordinary => !m.IsImplicitlyDeclared,
                MethodKind.Constructor => !m.IsImplicitlyDeclared || hasInitializers,
                _ => false
            })
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
                var declSyntax = syntaxRef?.GetSyntax() as BaseMethodDeclarationSyntax;

                // The semantic model must match the method's own tree — a
                // partial class puts members in trees the type walk never saw.
                var throws = declSyntax == null
                    ? new List<ThrownType>()
                    : ExtractThrows(declSyntax, compilation.GetSemanticModel(declSyntax.SyntaxTree));

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
                    // A test class's ctor is xUnit's primary setup hook — the
                    // framework runs it before every test, no test calls it.
                    IsTestLifecycle = hasTests
                        && (IsLifecycleMethod(m) || m.MethodKind == MethodKind.Constructor),
                    // Interface members and abstract methods have no body. They are
                    // still nodes worth having (calls bind to them), but they are not
                    // code that a test could execute.
                    IsAbstract = m.IsAbstract,
                    NestingDepth = declSyntax == null ? 0 : BodyGraphExtractor.NestingDepth(declSyntax),
                    Throws = throws,
                    EntryPointKind = ClassifyEntryPoint(m, inScope),
                    ExtendsTypeFullName = m.IsExtensionMethod
                        ? m.Parameters[0].Type.OriginalDefinition.ToDisplayString()
                        : null,
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
    /// caller gates on that, and also flags test-class constructors, which this
    /// predicate does not see. A base-class hook is still missed: it is extracted
    /// under the base type, which has no tests of its own.
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
    /// The exception types that can leave this method locally: throw statements
    /// and throw expressions whose type no enclosing catch of the same method
    /// handles firmly. A bare rethrow contributes its catch's declared type —
    /// System.Exception for an untyped catch, the honest upper bound. Throws
    /// inside lambdas are attributed here as conditional — the lambda runs
    /// when the delegate runs, not when this method does, but the container is
    /// the only node it has. Local functions stay skipped: declared is not
    /// registered, and a dead local function would over-report. Assignability
    /// is decided on live symbols now, while both sides still are symbols —
    /// never by name at query time.
    /// </summary>
    private static List<ThrownType> ExtractThrows(BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var sites = new List<(ITypeSymbol? Type, SyntaxNode Site, bool InLambda)>();

        foreach (var node in decl.DescendantNodes(n => n == decl || n is not LocalFunctionStatementSyntax))
        {
            var inLambda = node.Ancestors().TakeWhile(a => a != decl)
                .Any(a => a is AnonymousFunctionExpressionSyntax);

            switch (node)
            {
                case ThrowStatementSyntax { Expression: { } thrown } statement:
                    sites.Add((model.GetTypeInfo(thrown).Type, statement, inLambda));
                    break;

                case ThrowStatementSyntax rethrow:
                    sites.Add((RethrownType(rethrow, decl, model), rethrow, inLambda));
                    break;

                case ThrowExpressionSyntax { Expression: { } thrown } expression:
                    sites.Add((model.GetTypeInfo(thrown).Type, expression, inLambda));
                    break;
            }
        }

        var escaping = new List<ThrownType>();

        foreach (var (type, site, inLambda) in sites)
        {
            if (type is not INamedTypeSymbol named || named.TypeKind == TypeKind.Error) continue;

            var conditional = inLambda;
            var handled = false;

            // The walk stops at a lambda boundary as well as at the method: a
            // try in the container encloses the lambda's *creation*, not its
            // later execution, so only trys inside the same lambda guard it.
            foreach (var tryStatement in site.Ancestors()
                .TakeWhile(a => a != decl && a is not AnonymousFunctionExpressionSyntax)
                .OfType<TryStatementSyntax>())
            {
                // A throw inside a catch or finally answers to the outer trys only.
                if (!tryStatement.Block.Span.Contains(site.Span)) continue;

                foreach (var catchClause in tryStatement.Catches)
                {
                    if (!CatchMatches(catchClause, named, model)) continue;
                    if (catchClause.Filter == null) { handled = true; break; }
                    conditional = true; // the filter may decline at runtime
                }
                if (handled) break;
            }

            if (!handled)
                escaping.Add(new ThrownType(named.ToDisplayString(), AncestorChain(named), conditional));
        }

        // One record per type; an unconditional escape outranks a conditional one.
        return escaping
            .GroupBy(t => t.Type, StringComparer.Ordinal)
            .Select(g => g.OrderBy(t => t.Conditional).First())
            .ToList();
    }

    /// <summary>The type a bare 'throw;' rethrows: its catch's declaration, else System.Exception.</summary>
    private static ITypeSymbol? RethrownType(
        ThrowStatementSyntax rethrow, BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var catchClause = rethrow.Ancestors()
            .TakeWhile(a => a != decl && a is not AnonymousFunctionExpressionSyntax)
            .OfType<CatchClauseSyntax>().FirstOrDefault();
        return catchClause?.Declaration?.Type is { } typeSyntax
            ? model.GetTypeInfo(typeSyntax).Type
            : model.Compilation.GetTypeByMetadataName("System.Exception");
    }

    private static bool CatchMatches(CatchClauseSyntax catchClause, INamedTypeSymbol thrown, SemanticModel model)
    {
        if (catchClause.Declaration?.Type is not { } typeSyntax) return true; // untyped catch-all
        if (model.GetTypeInfo(typeSyntax).Type is not INamedTypeSymbol caught) return false;

        for (ITypeSymbol? t = thrown; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, caught)) return true;
        return false;
    }

    /// <summary>Self first, base types after, stopping at System.Exception — what a catch set is matched against.</summary>
    private static List<string> AncestorChain(INamedTypeSymbol type)
    {
        var chain = new List<string>();
        for (ITypeSymbol? t = type; t != null; t = t.BaseType)
        {
            var name = t.ToDisplayString();
            chain.Add(name);
            if (name == "System.Exception") break;
        }
        return chain;
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
    private static string? ClassifyEntryPoint(IMethodSymbol m, IReadOnlySet<string> inScope)
    {
        if (m.MethodKind != MethodKind.Ordinary) return null;

        // Test methods and their hooks are framework-invoked too, but the test
        // host catches everything they throw — that is its job — so calling
        // them escape surfaces would only manufacture noise.
        if (IsTestMethod(m)) return null;

        if (m.IsStatic && m.Name == "Main") return "main";

        if (!m.IsStatic && m.DeclaredAccessibility == Accessibility.Public
            && PageHandlerPrefixes.Any(p => m.Name.StartsWith(p, StringComparison.Ordinal))
            && BaseChainHasName(m.ContainingType, "PageModel"))
            return "pageHandler";

        if (!m.IsStatic && m.DeclaredAccessibility == Accessibility.Public && IsControllerType(m.ContainingType))
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

        // Implementing an interface a framework declared is the registration
        // pattern: IMiddleware, IValueConverter, IHostedService — the
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

    /// <summary>Mirrors ClassifySymbol's controller heuristic so the two never disagree.</summary>
    private static bool IsControllerType(INamedTypeSymbol type) =>
        (type.BaseType?.ToDisplayString() ?? "").Contains("Controller")
        || type.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute");

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
    /// Resolve call sites to (caller, callee) method-id pairs with the
    /// exception guards enclosing each site. Only calls whose target is
    /// declared in one of the loaded projects are returned -- an edge to
    /// String.Format would be noise, not navigation.
    ///
    /// Membership is tested by assembly *name*, not symbol identity. A call into
    /// a sibling project may bind to either a source symbol or a metadata symbol
    /// depending on how the workspace resolved the reference, and those are not
    /// reference-equal; the name is the same either way, and so is the MethodId
    /// the node was registered under.
    ///
    /// Besides explicit invocations, two compiler-emitted calls carry edges: a
    /// new-expression is a call to the constructor, and a using/await using is a
    /// call to the resource's Dispose/DisposeAsync. Neither has invocation
    /// syntax at the site, and without the edges the methods read as unreached
    /// by the very code that guarantees they run.
    /// </summary>
    public IEnumerable<CallSiteInfo> ExtractCallSites()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = _loaded
            .Select(l => l.Compilation.Assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var site in root.DescendantNodes()
                .OfType<BaseMethodDeclarationSyntax>()
                .SelectMany(decl => MethodCallSites(decl, model, inScope)))
            {
                yield return site;
            }

            foreach (var site in root.DescendantNodes()
                .OfType<TypeDeclarationSyntax>()
                .SelectMany(typeDecl => InitializerCallSites(typeDecl, model, inScope)))
            {
                yield return site;
            }
        }
    }

    /// <summary>
    /// Call sites in one method declaration: explicit invocations and
    /// new-expressions with their enclosing catch guards, plus the implicit
    /// Dispose/DisposeAsync of everything the method holds in a using. Dispose
    /// sites carry no guards on purpose — disposal runs at scope exit, where
    /// relating it to enclosing trys is subtle, and unguarded is the
    /// conservative reading for escape analysis.
    /// </summary>
    private static IEnumerable<CallSiteInfo> MethodCallSites(
        BaseMethodDeclarationSyntax decl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        if (model.GetDeclaredSymbol(decl) is not IMethodSymbol caller) yield break;
        var fromId = MethodId(caller);

        foreach (var (site, target) in CallTargetSites(decl, model))
        {
            if (InScopeMethodId(target, inScope) is not { } toId) continue;
            if (toId == fromId) continue; // direct recursion adds no navigational value

            var (guardedBy, filteredBy) = SiteGuards(site, decl, model);
            yield return new CallSiteInfo(fromId, toId, guardedBy, filteredBy);
        }

        // A method group handed somewhere is a call the future makes. The edge
        // carries no guards on purpose: a try around the registration site does
        // not catch what the delegate throws when it finally runs.
        foreach (var (_, target) in MethodGroupSites(decl, model))
        {
            if (InScopeMethodId(target, inScope) is not { } toId) continue;
            if (toId == fromId) continue;

            yield return new CallSiteInfo(
                fromId, toId, Array.Empty<string>(), Array.Empty<string>(), IsDelegate: true);
        }

        foreach (var (resourceType, isAsync) in DisposedResources(decl, model))
        {
            if (ResolveDisposeMethod(resourceType, isAsync) is not { } dispose) continue;
            if (InScopeMethodId(dispose, inScope) is not { } toId) continue;
            if (toId == fromId) continue;

            yield return new CallSiteInfo(fromId, toId, Array.Empty<string>(), Array.Empty<string>());
        }
    }

    /// <summary>
    /// Call sites for one type's field and property initializers. Initializers
    /// execute inside the instance constructors, not inside any method
    /// declaration, so the method walk in ExtractCallSites never sees them.
    /// Their calls are attributed to every instance ctor of the type —
    /// including the implicit default ctor, which in this case is exactly the
    /// code that runs them. (Overapproximation: a ctor chaining this(...) does
    /// not rerun initializers, but the work is still one ctor away.) An
    /// initializer cannot contain a try, so the sites are always unguarded.
    /// </summary>
    private static IEnumerable<CallSiteInfo> InitializerCallSites(
        TypeDeclarationSyntax typeDecl, SemanticModel model, IReadOnlySet<string> inScope)
    {
        var initializers = InstanceInitializers(typeDecl).ToList();
        if (initializers.Count == 0) yield break;
        if (model.GetDeclaredSymbol(typeDecl) is not INamedTypeSymbol type) yield break;

        var ctorIds = type.InstanceConstructors.Select(MethodId).Distinct().ToList();

        foreach (var initializer in initializers)
            foreach (var (_, target) in CallTargetSites(initializer, model))
            {
                if (InScopeMethodId(target, inScope) is not { } toId) continue;

                foreach (var ctorId in ctorIds)
                    if (toId != ctorId)
                        yield return new CallSiteInfo(
                            ctorId, toId, Array.Empty<string>(), Array.Empty<string>());
            }
    }

    /// <summary>
    /// Method-group references in one scope — an identifier or member access
    /// naming a method where a delegate is expected. The conversion type is
    /// the discriminator: a real method group converts to a delegate type,
    /// which is exactly what excludes nameof and plain member reads.
    /// </summary>
    private static IEnumerable<(SyntaxNode Site, IMethodSymbol Target)> MethodGroupSites(
        SyntaxNode scope, SemanticModel model)
    {
        foreach (var node in scope.DescendantNodesAndSelf())
        {
            if (node is not (IdentifierNameSyntax or MemberAccessExpressionSyntax))
                continue;

            // The name inside an invocation or member access is not itself a
            // method group; only the outermost expression converts.
            if (node.Parent is InvocationExpressionSyntax invocation && invocation.Expression == node)
                continue;
            if (node.Parent is MemberAccessExpressionSyntax) continue;

            if (model.GetTypeInfo(node).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                continue;

            var info = model.GetSymbolInfo(node);
            var target = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (target != null) yield return (node, target);
        }
    }

    /// <summary>
    /// Methods that out-of-solution code can call back into: the target of a
    /// method group, or the container of a lambda, handed to a receiver
    /// declared outside the loaded projects — a framework ctor or method
    /// argument, or an assignment to an external property or event. The
    /// container stands in for its lambdas because a lambda has no node of its
    /// own; its calls and throws are already attributed there, so marking the
    /// container makes exactly those facts escapable (a deliberate
    /// over-approximation — the container's other throws ride along). One hop
    /// only: a delegate stored and forwarded elsewhere is not tracked.
    /// </summary>
    public IEnumerable<string> ExtractCallbackTargets()
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        var inScope = _loaded
            .Select(l => l.Compilation.Assembly.Name)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var (root, model) in _loaded.SelectMany(
                     l => l.Compilation.SyntaxTrees.Select(t => (t.GetRoot(), l.Compilation.GetSemanticModel(t)))))
        {
            foreach (var decl in root.DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(decl) is not IMethodSymbol container) continue;

                foreach (var (site, target) in MethodGroupSites(decl, model))
                {
                    if (!RegisteredOutOfSolution(site, model, inScope)) continue;
                    if (InScopeMethodId(target, inScope) is { } targetId) yield return targetId;
                }

                foreach (var lambda in decl.DescendantNodes().OfType<AnonymousFunctionExpressionSyntax>())
                {
                    if (model.GetTypeInfo(lambda).ConvertedType is not INamedTypeSymbol { TypeKind: TypeKind.Delegate })
                        continue;
                    if (RegisteredOutOfSolution(lambda, model, inScope))
                    {
                        yield return MethodId(container);
                        break; // one stamp per container is enough
                    }
                }
            }
        }
    }

    /// <summary>
    /// Whether a delegate-valued expression lands in out-of-solution hands:
    /// an argument to an external invocation or construction, or the right
    /// side of an assignment (including +=) whose left side is external.
    /// Anything else — a local, a field of this solution, a return — stays
    /// in-solution and answers false: the delegate edge still connects the
    /// chain, it just is not a framework entry surface.
    /// </summary>
    private static bool RegisteredOutOfSolution(
        SyntaxNode registration, SemanticModel model, IReadOnlySet<string> inScope)
    {
        for (SyntaxNode? node = registration; node is not null and not StatementSyntax; node = node.Parent)
        {
            switch (node.Parent)
            {
                case ArgumentSyntax argument:
                    var call = argument.Ancestors().FirstOrDefault(a =>
                        a is InvocationExpressionSyntax or BaseObjectCreationExpressionSyntax);
                    if (call == null) return false;
                    var callInfo = model.GetSymbolInfo(call);
                    var callee = callInfo.Symbol as IMethodSymbol
                        ?? callInfo.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
                    return SymbolOutOfSolution(callee, inScope);

                case AssignmentExpressionSyntax assignment when assignment.Right == node:
                    return SymbolOutOfSolution(model.GetSymbolInfo(assignment.Left).Symbol, inScope);

                case EqualsValueClauseSyntax:
                    return false; // initializing a local or field of this solution
            }
        }
        return false;
    }

    private static bool SymbolOutOfSolution(ISymbol? symbol, IReadOnlySet<string> inScope) =>
        symbol?.ContainingAssembly?.Name is { } assembly && !inScope.Contains(assembly);

    /// <summary>
    /// The catch guards enclosing one call site: types caught firmly by
    /// enclosing trys ("*" for an untyped catch-all), and types caught only
    /// behind a when filter — a filter may decline at runtime, so it counts as
    /// conditional handling, never as handling. Only trys whose *block* holds
    /// the site guard it; a site inside a catch or finally answers to the
    /// outer trys alone.
    /// </summary>
    private static (List<string> GuardedBy, List<string> FilteredBy) SiteGuards(
        SyntaxNode site, BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        var guardedBy = new List<string>();
        var filteredBy = new List<string>();

        foreach (var tryStatement in site.Ancestors().TakeWhile(a => a != decl).OfType<TryStatementSyntax>())
        {
            if (!tryStatement.Block.Span.Contains(site.Span)) continue;

            foreach (var catchClause in tryStatement.Catches)
            {
                string caught;
                if (catchClause.Declaration?.Type is { } typeSyntax)
                {
                    if (model.GetTypeInfo(typeSyntax).Type is not { } caughtType) continue;
                    caught = caughtType.ToDisplayString();
                }
                else
                {
                    caught = "*";
                }

                (catchClause.Filter == null ? guardedBy : filteredBy).Add(caught);
            }
        }

        return (guardedBy, filteredBy);
    }

    /// <summary>
    /// The control-flow graph of one method's body, located by the same id its
    /// Method node is registered under. Null when the id matches nothing or the
    /// method has no body. Requires a loaded project — body graphs are computed
    /// from the live compilation, not from a serialized graph.
    /// </summary>
    public BodyGraph? GetMethodBodyGraph(string methodId)
    {
        if (_loaded.Count == 0) throw new InvalidOperationException("Load a project first.");

        foreach (var loaded in _loaded)
        {
            foreach (var tree in loaded.Compilation.SyntaxTrees)
            {
                SemanticModel? model = null;
                foreach (var decl in tree.GetRoot().DescendantNodes().OfType<BaseMethodDeclarationSyntax>())
                {
                    model ??= loaded.Compilation.GetSemanticModel(tree);
                    if (model.GetDeclaredSymbol(decl) is not IMethodSymbol symbol) continue;
                    if (MethodId(symbol) != methodId) continue;

                    return BodyGraphExtractor.Extract(model, decl, MethodId);
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Every method a scope calls, with the syntax node of each site: explicit
    /// invocations, plus new-expressions — a constructor call with no
    /// invocation syntax at the site. CandidateSymbols covers calls the
    /// compiler could not fully bind (an overload set narrowed by a dynamic or
    /// erroneous argument).
    /// </summary>
    private static IEnumerable<(SyntaxNode Site, IMethodSymbol Target)> CallTargetSites(
        SyntaxNode scope, SemanticModel model)
    {
        foreach (var node in scope.DescendantNodesAndSelf())
        {
            if (node is not InvocationExpressionSyntax and not BaseObjectCreationExpressionSyntax)
                continue;

            var info = model.GetSymbolInfo(node);
            var target = info.Symbol as IMethodSymbol
                ?? info.CandidateSymbols.OfType<IMethodSymbol>().FirstOrDefault();
            if (target != null) yield return (node, target);
        }
    }

    /// <summary>
    /// MethodId for a call target, or null when the target is declared outside
    /// the loaded projects — an edge to String.Format is noise, not navigation.
    /// </summary>
    private static string? InScopeMethodId(IMethodSymbol target, IReadOnlySet<string> inScope)
    {
        var def = target.OriginalDefinition;
        var assembly = def.ContainingAssembly?.Name;
        if (assembly == null || !inScope.Contains(assembly)) return null;
        return MethodId(def);
    }

    /// <summary>Symbol-side gate for the implicit-ctor node decision.</summary>
    private static bool HasInstanceInitializers(INamedTypeSymbol symbol) =>
        symbol.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax())
            .OfType<TypeDeclarationSyntax>()
            .Any(t => InstanceInitializers(t).Any());

    /// <summary>
    /// Instance field and property initializers of one type declaration. Static
    /// initializers are excluded on purpose: they run in the static ctor, which
    /// has no syntactic call sites and is not a graph node.
    /// </summary>
    private static IEnumerable<EqualsValueClauseSyntax> InstanceInitializers(TypeDeclarationSyntax typeDecl)
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
    /// Every resource a method disposes implicitly: using statements (block and
    /// expression forms) and using declarations, with await variants marked so
    /// the caller resolves DisposeAsync rather than Dispose.
    /// </summary>
    private static IEnumerable<DisposedResource> DisposedResources(
        BaseMethodDeclarationSyntax decl, SemanticModel model)
    {
        foreach (var node in decl.DescendantNodes())
        {
            switch (node)
            {
                case UsingStatementSyntax u:
                    foreach (var resource in UsingStatementResources(u, model))
                        yield return resource;
                    break;

                case LocalDeclarationStatementSyntax l when l.UsingKeyword.IsKind(SyntaxKind.UsingKeyword):
                    foreach (var resource in DeclaredResources(l.Declaration, model, l.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword)))
                        yield return resource;
                    break;
            }
        }
    }

    /// <summary>
    /// Resources of one using statement: the declared variables when it has a
    /// declaration, otherwise the expression form's single resource.
    /// </summary>
    private static IEnumerable<DisposedResource> UsingStatementResources(
        UsingStatementSyntax u, SemanticModel model)
    {
        var isAsync = u.AwaitKeyword.IsKind(SyntaxKind.AwaitKeyword);

        if (u.Declaration != null)
        {
            foreach (var resource in DeclaredResources(u.Declaration, model, isAsync))
                yield return resource;
            yield break;
        }

        if (u.Expression != null && model.GetTypeInfo(u.Expression).Type is { } expressionType)
            yield return new DisposedResource(expressionType, isAsync);
    }

    /// <summary>Typed locals of one using declaration's variable list.</summary>
    private static IEnumerable<DisposedResource> DeclaredResources(
        VariableDeclarationSyntax declaration, SemanticModel model, bool isAsync)
    {
        foreach (var variable in declaration.Variables)
            if (model.GetDeclaredSymbol(variable) is ILocalSymbol local)
                yield return new DisposedResource(local.Type, isAsync);
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

/// <summary>
/// One resolved call site, caller to callee by method id, with the exception
/// guards enclosing the site. <paramref name="GuardedBy"/> holds types caught
/// firmly by enclosing trys ("*" for an untyped catch-all);
/// <paramref name="FilteredBy"/> holds types caught only behind a when filter.
/// <paramref name="IsDelegate"/> marks a method-group reference rather than an
/// invocation — a call the future makes, always unguarded. A named type
/// rather than a tuple so the two ids cannot be swapped silently at a call
/// site.
/// </summary>
public sealed record CallSiteInfo(
    string FromId,
    string ToId,
    IReadOnlyList<string> GuardedBy,
    IReadOnlyList<string> FilteredBy,
    bool IsDelegate = false);

/// <summary>
/// One resource a method disposes implicitly, and whether the disposal is
/// await using — which resolves DisposeAsync rather than Dispose.
/// </summary>
public readonly record struct DisposedResource(ITypeSymbol Type, bool IsAsync);

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

    /// <summary>
    /// Maximum syntactic nesting depth of the body — the christmas-tree metric.
    /// 0 for expression-bodied, abstract, and implicitly declared members.
    /// </summary>
    public int NestingDepth { get; init; }

    /// <summary>Exception types that can leave this method locally; see ExtractThrows.</summary>
    public List<ThrownType> Throws { get; init; } = new();

    /// <summary>Entry-point classification, or null for an ordinary method; see ClassifyEntryPoint.</summary>
    public string? EntryPointKind { get; init; }

    /// <summary>
    /// For an extension method, the full name of the type it extends — the
    /// this parameter's type. An extension is part of the extended type's
    /// working surface even though it is declared elsewhere, and the graph
    /// says so with an Extends edge.
    /// </summary>
    public string? ExtendsTypeFullName { get; init; }

    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
}

/// <summary>
/// One exception type escaping a method, with its base-type chain (self first,
/// stopping at System.Exception) resolved at extraction — the only moment both
/// the thrown and the caught side are live symbols, so assignability becomes a
/// set-membership test instead of a name heuristic. Conditional means every
/// local catch that would take it carries a when filter.
/// </summary>
public sealed record ThrownType(string Type, IReadOnlyList<string> AncestorChain, bool Conditional);

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
