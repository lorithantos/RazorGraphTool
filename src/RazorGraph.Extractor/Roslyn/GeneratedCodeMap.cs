namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Maps generated code back to the source it was compiled from: the
/// RazorCompiledItem attribute as the authoritative type-to-source map, with
/// the #line majority vote as the fallback for generated trees without one.
/// </summary>
internal static class GeneratedCodeMap
{
    /// <summary>
    /// The authoritative answer comes from the assembly-level
    /// [RazorCompiledItem(type, kind, "/Pages/….cshtml")] the generator emits
    /// per compiled file — an exact type→source map, resolved against the
    /// project directory. The #line majority vote is only the fallback for
    /// generated trees without one.
    /// </summary>
    internal static string? GeneratedSourcePath(
        INamedTypeSymbol symbol,
        Dictionary<INamedTypeSymbol, string> compiledItems,
        string? projectFilePath,
        SyntaxTree tree)
    {
        if (compiledItems.TryGetValue(symbol, out var identifier)
            && Path.GetDirectoryName(projectFilePath) is { } projectDir)
        {
            return Path.GetFullPath(Path.Combine(projectDir, identifier.TrimStart('/', '\\')));
        }
        return MappedSourcePath(tree);
    }

    internal static Dictionary<INamedTypeSymbol, string> RazorCompiledItems(Compilation compilation)
    {
        var map = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default);
        foreach (var attr in compilation.Assembly.GetAttributes())
        {
            if (attr.AttributeClass?.Name != "RazorCompiledItemAttribute") continue;
            if (attr.ConstructorArguments.Length < 3) continue;
            if (attr.ConstructorArguments[0].Value is not INamedTypeSymbol type) continue;
            if (attr.ConstructorArguments[2].Value is not string identifier) continue;
            map[type] = identifier;
        }
        return map;
    }

    /// <summary>
    /// Whether an attribute was written by the build rather than a person. The
    /// SDK generates AssemblyInfo.cs and .NETCoreApp…AssemblyAttributes.cs into
    /// obj\ as plain .cs files, so the .g.cs suffix alone misses them — the obj
    /// path segment is the tell, the same rule the client-asset scan applies.
    /// A site with no syntax reference at all is compiler-synthesized: also not
    /// authored. Roughly ten assembly attributes per project ride on this test.
    /// </summary>
    internal static bool IsGeneratedSite(AttributeData attribute)
    {
        var path = attribute.ApplicationSyntaxReference?.SyntaxTree.FilePath;
        if (path == null) return true;
        if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) return true;
        return path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment.Equals("obj", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Fallback source attribution: the file named by the MAJORITY of a
    /// generated tree's #line directives, not the first. The Razor generator
    /// inlines _ViewImports content at the top of every generated file, each
    /// import under its own #line naming _ViewImports.cshtml — so "first
    /// directive" attributes every page to the imports file (found on
    /// ImageSelectorV2, 2026-08-08), and near-empty files can even lose the
    /// majority. That is why the RazorCompiledItem attribute is consulted
    /// first; this survives only for generated trees without one.
    /// </summary>
    internal static string? MappedSourcePath(SyntaxTree tree) =>
        tree.GetRoot().DescendantNodes(descendIntoTrivia: true)
            .OfType<LineOrSpanDirectiveTriviaSyntax>()
            .Select(d => d.File.ValueText)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();
}
