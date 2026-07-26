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
    private Compilation? _compilation;
    private Project? _project;

    // Must run before any Microsoft.Build type is JITed; the ctor body is safe
    // because MSBuild types are first referenced inside LoadProjectAsync.
    public RoslynExtractor() => EnsureMsBuildRegistered();

    public static void EnsureMsBuildRegistered()
    {
        if (!MSBuildLocator.IsRegistered) MSBuildLocator.RegisterDefaults();
    }

    /// <summary>
    /// The compilation from the most recent load, for consumers that need
    /// symbol-level analysis (e.g., tag helper discovery).
    /// </summary>
    public Compilation? Compilation => _compilation;

    /// <summary>File path of the loaded project, for locating its Razor files.</summary>
    public string? ProjectFilePath => _project?.FilePath;

    public async Task<Compilation> LoadProjectAsync(string projectPath, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        _project = await _workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
        _compilation = await _project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectPath}");
        return _compilation;
    }

    public async Task<Compilation> LoadSolutionAsync(string solutionPath, string projectName, CancellationToken ct = default)
    {
        _workspace = MSBuildWorkspace.Create();
        var solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        _project = solution.Projects.FirstOrDefault(p =>
            p.Name.Equals(projectName, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Project '{projectName}' not found in solution.");
        _compilation = await _project.GetCompilationAsync(ct)
            ?? throw new InvalidOperationException($"Failed to compile project: {projectName}");
        return _compilation;
    }

    /// <summary>
    /// Extract all relevant symbols: PageModels, Controllers, Services, ViewModels.
    /// </summary>
    public IEnumerable<SymbolInfo> ExtractSymbols()
    {
        if (_compilation == null) throw new InvalidOperationException("Load a project first.");

        foreach (var tree in _compilation.SyntaxTrees)
        {
            var model = _compilation.GetSemanticModel(tree);
            var root = tree.GetRoot();

            foreach (var typeDecl in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var symbol = model.GetDeclaredSymbol(typeDecl);
                if (symbol == null) continue;

                var info = ClassifySymbol(symbol);
                if (info != null) yield return info;
            }
        }
    }

    private SymbolInfo? ClassifySymbol(INamedTypeSymbol symbol)
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
                Type = NodeType.PageModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Properties = ExtractProperties(symbol),
                Methods = ExtractMethods(symbol),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Controller detection
        if (baseType.Contains("Controller") || symbol.GetAttributes().Any(a => a.AttributeClass?.Name == "ApiControllerAttribute"))
        {
            return new SymbolInfo
            {
                Id = $"ctrl:{symbol.ToDisplayString()}",
                Type = NodeType.ApiController,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                BaseType = baseType,
                Methods = ExtractControllerActions(symbol),
                InjectedServices = ExtractInjectedServices(symbol)
            };
        }

        // Service detection (heuristic: ends with Service, or implements interface ending with Service)
        if (symbol.Name.EndsWith("Service") || interfaces.Any(i => i.EndsWith("Service")))
        {
            return new SymbolInfo
            {
                Id = $"svc:{symbol.ToDisplayString()}",
                Type = symbol.TypeKind == TypeKind.Interface ? NodeType.ServiceInterface : NodeType.ServiceImplementation,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                ImplementedInterfaces = interfaces.Where(i => i.EndsWith("Service")).ToList(),
                Methods = ExtractMethods(symbol)
            };
        }

        // ViewModel detection (heuristic: ends with VM or ViewModel, or used in @model directives)
        if (symbol.Name.EndsWith("VM") || symbol.Name.EndsWith("ViewModel") || symbol.Name.EndsWith("Model"))
        {
            return new SymbolInfo
            {
                Id = $"vm:{symbol.ToDisplayString()}",
                Type = NodeType.ViewModel,
                Name = symbol.Name,
                FullName = symbol.ToDisplayString(),
                FilePath = symbol.Locations.FirstOrDefault()?.SourceTree?.FilePath,
                LineStart = lineStart,
                LineEnd = lineEnd,
                Properties = ExtractProperties(symbol)
            };
        }

        return null;
    }

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
    public string? FilePath { get; init; }
    public int? LineStart { get; init; }
    public int? LineEnd { get; init; }
    public string? BaseType { get; init; }
    public List<string> ImplementedInterfaces { get; init; } = new();
    public List<string> InjectedServices { get; init; } = new();
    public List<PropertyInfo> Properties { get; init; } = new();
    public List<MethodInfo> Methods { get; init; } = new();
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
