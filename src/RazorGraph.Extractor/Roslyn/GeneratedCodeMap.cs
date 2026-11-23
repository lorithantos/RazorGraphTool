namespace RazorGraph.Extractor.Roslyn;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

/// <summary>
/// Maps generated code back to the source it was compiled from: the
/// RazorCompiledItem attribute as the authoritative type-to-source map, then
/// the #pragma checksum the Razor generator heads every file with, and the
/// #line majority vote as the last fallback for generated trees with neither.
/// </summary>
internal static class GeneratedCodeMap
{
    /// <summary>
    /// The authoritative answer comes from the assembly-level
    /// [RazorCompiledItem(type, kind, "/Pages/….cshtml")] the generator emits
    /// per compiled file — an exact type→source map, resolved against the
    /// project directory. Blazor components carry no such attribute, so for
    /// them the tree's own #pragma checksum answers, and the #line majority
    /// vote only for generated trees without either.
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
        return ChecksumSourcePath(tree) ?? MappedSourcePath(tree, symbol.Name);
    }

    /// <summary>
    /// The .razor or .cshtml file the Razor generator names in the #pragma
    /// checksum it writes as a generated file's first line -- the file it
    /// compiled, stated by the generator, where the #line directives only say
    /// where each piece of C# came from. A markup-only component is why this
    /// is consulted before the vote: its class holds no C# from its own file,
    /// so its only #line directives are the @using and @inject lines copied in
    /// from _Imports.razor, and the vote joined the class to the imports file
    /// (GreetingBadge, 2026-11-23). Null when the head of the tree names no
    /// Razor file, or names more than one -- left to the vote, not guessed.
    /// </summary>
    internal static string? ChecksumSourcePath(SyntaxTree tree)
    {
        var named = tree.GetRoot().GetLeadingTrivia()
            .Select(t => t.GetStructure())
            .OfType<PragmaChecksumDirectiveTriviaSyntax>()
            .Select(d => d.File.ValueText)
            .Where(IsRazorSource)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(2)
            .ToList();
        return named.Count == 1 ? named[0] : null;
    }

    private static bool IsRazorSource(string path) =>
        path.EndsWith(".razor", StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase);

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
    /// majority. That is why the RazorCompiledItem attribute and the checksum
    /// are consulted first; this survives only for generated trees without
    /// either.
    /// <para>
    /// An imports file (_Imports.razor, _ViewImports.cshtml) has no vote unless
    /// the class is that file's own (<c>_Imports</c>, or the mangled
    /// <c>Pages__ViewImports</c>): its lines are copied into every file under
    /// it, so a class with no C# of its own would otherwise always land on it.
    /// With nothing else left the answer is null -- unlinked, not guessed.
    /// </para>
    /// </summary>
    internal static string? MappedSourcePath(SyntaxTree tree, string className) =>
        tree.GetRoot().DescendantNodes(descendIntoTrivia: true)
            .OfType<LineOrSpanDirectiveTriviaSyntax>()
            .Select(d => d.File.ValueText)
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Where(f => !IsImportsFile(f) || IsImportsClass(className, f))
            .GroupBy(f => f, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Key)
            .FirstOrDefault();

    private static bool IsImportsFile(string path) =>
        Path.GetFileName(path) is var name
        && (name.Equals("_Imports.razor", StringComparison.OrdinalIgnoreCase)
            || name.Equals("_ViewImports.cshtml", StringComparison.OrdinalIgnoreCase));

    private static bool IsImportsClass(string className, string importsPath) =>
        Path.GetFileNameWithoutExtension(importsPath) is var stem
        && (className.Equals(stem, StringComparison.Ordinal)
            || className.EndsWith("_" + stem, StringComparison.Ordinal));
}
