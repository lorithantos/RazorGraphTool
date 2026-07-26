namespace RazorGraph.Extractor.Razor;

using Microsoft.AspNetCore.Mvc.Razor.Extensions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using RazorSyntaxNode = Microsoft.AspNetCore.Razor.Language.Syntax.SyntaxNode;

/// <summary>
/// Extracts Razor-specific structure from .cshtml and .razor files
/// using the official Razor parser. The Syntax API it walks is internal to
/// Microsoft.AspNetCore.Razor.Language and is unlocked via Krafs.Publicizer;
/// when the syntax path fails, extraction falls back to TextRazorExtractor.
/// </summary>
public sealed class RazorExtractor
{
    private readonly RazorProjectEngine _engine;
    private readonly string _projectRoot;
    private readonly TextRazorExtractor _fallback = new();
    private IReadOnlyList<TagHelperDescriptor> _tagHelpers = Array.Empty<TagHelperDescriptor>();
    private bool _warnedFallback;

    public RazorExtractor(string projectRoot)
    {
        _projectRoot = projectRoot;
        var fileSystem = RazorProjectFileSystem.Create(projectRoot);
        _engine = RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem, builder =>
        {
            // Registers the MVC directives (@page, @model, @inject, @section, ...);
            // without this the parser leaves them as plain markup.
            RazorExtensions.Register(builder);
        });
    }

    /// <summary>
    /// Tag helper descriptors discovered from the project compilation. When present,
    /// the parser binds tag helper elements so they appear as dedicated syntax nodes.
    /// </summary>
    public void SetTagHelpers(IReadOnlyList<TagHelperDescriptor> tagHelpers) => _tagHelpers = tagHelpers;

    public RazorPageInfo ExtractPage(string filePath) =>
        ExtractFromText(File.ReadAllText(filePath), filePath, GetRelativePath(filePath));

    public RazorPageInfo ExtractFromText(string text, string filePath, string relativePath)
    {
        try
        {
            return ExtractWithSyntaxTree(text, filePath, relativePath);
        }
        catch (Exception ex)
        {
            if (!_warnedFallback)
            {
                _warnedFallback = true;
                Console.Error.WriteLine(
                    $"Warning: Razor syntax parsing failed ({ex.GetType().Name}: {ex.Message}); " +
                    "falling back to text analysis for affected files.");
                if (Environment.GetEnvironmentVariable("RAZORGRAPH_DEBUG") == "1")
                    Console.Error.WriteLine(ex.ToString());
            }
            return _fallback.ExtractFromText(text, filePath, relativePath);
        }
    }

    private RazorPageInfo ExtractWithSyntaxTree(string text, string filePath, string relativePath)
    {
        // Both paths must be non-null: codegen passes write RelativePath-derived metadata
        // as string literals and NRE on null. Design-time processing is used because we
        // only need the syntax tree and tag helper binding, not runtime codegen.
        var source = RazorSourceDocument.Create(text, new RazorSourceDocumentProperties(filePath, relativePath));
        var codeDocument = _engine.ProcessDesignTime(source, FileKinds.Legacy, Array.Empty<RazorSourceDocument>(), _tagHelpers);
        var syntaxTree = codeDocument.GetSyntaxTree();
        var root = syntaxTree.Root;

        // Syntax spans index into the original text; the regex sub-extractions run on
        // comment-stripped text (same line count) so @* ... *@ can't create false positives.
        var sourceLines = new LineMap(text);
        var stripped = RazorTextScanners.StripRazorComments(text);
        var scanLines = new LineMap(stripped);

        var tagHelpers = ExtractTagHelperInvocations(root, sourceLines);
        // The parser only produces tag helper nodes when descriptors were bound;
        // the text scan keeps asp-* extraction working either way.
        foreach (var scanned in RazorTextScanners.ScanAspTagHelpers(stripped, scanLines))
        {
            if (!tagHelpers.Any(t => t.TagName == scanned.TagName && t.Line == scanned.Line))
                tagHelpers.Add(scanned);
        }

        return new RazorPageInfo
        {
            Id = $"page:{relativePath}",
            FilePath = filePath,
            RelativePath = relativePath,
            IsPage = IsRazorPage(root),
            RouteTemplate = ExtractRouteTemplate(root),
            ModelType = ExtractModelDirective(root),
            // @layout is a component directive not registered for MVC parsing, so the
            // scanners cover both it and the cshtml Layout = "..." assignment form.
            Layout = ExtractLayout(root)
                ?? RazorTextScanners.ScanLayoutDirective(stripped)
                ?? RazorTextScanners.ScanLayoutAssignment(stripped),
            InjectedServices = ExtractInjectDirectives(root),
            ViewDataKeys = RazorTextScanners.ScanViewDataKeys(stripped),
            Partials = RazorTextScanners.ScanPartialRenders(stripped, scanLines),
            TagHelpers = tagHelpers,
            Sections = ExtractSections(root)
        };
    }

    private static bool IsRazorPage(RazorSyntaxNode root) =>
        root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .Any(d => GetDirectiveName(d) == "page");

    private static string? ExtractRouteTemplate(RazorSyntaxNode root)
    {
        var pageDir = root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .FirstOrDefault(d => GetDirectiveName(d) == "page");

        return GetDirectiveBody(pageDir)?.Trim('"', '\'');
    }

    private static string? ExtractModelDirective(RazorSyntaxNode root)
    {
        var modelDir = root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .FirstOrDefault(d => GetDirectiveName(d) == "model");

        return GetDirectiveBody(modelDir);
    }

    private static string? ExtractLayout(RazorSyntaxNode root)
    {
        var layoutDir = root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .FirstOrDefault(d => GetDirectiveName(d) == "layout");

        var body = GetDirectiveBody(layoutDir);
        return body?.Trim('"', '\'');
    }

    private static List<string> ExtractInjectDirectives(RazorSyntaxNode root) =>
        root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .Where(d => GetDirectiveName(d) == "inject")
            .Select(d => GetDirectiveBody(d))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()!;

    private static List<TagHelperInfo> ExtractTagHelperInvocations(RazorSyntaxNode root, LineMap lines)
    {
        var tagHelpers = new List<TagHelperInfo>();

        foreach (var element in root.DescendantNodes().OfType<MarkupTagHelperElementSyntax>())
        {
            var tagName = element.StartTag?.Name?.ToFullString()?.Trim() ?? "unknown";
            var attributes = element.StartTag?.Attributes
                .OfType<MarkupTagHelperAttributeSyntax>()
                .Select(a => new TagHelperAttributeInfo
                {
                    Name = a.Name?.ToFullString()?.Trim() ?? "",
                    Value = a.Value?.ToFullString()?.Trim('"', '\'', ' ') ?? ""
                })
                .ToList() ?? new List<TagHelperAttributeInfo>();

            tagHelpers.Add(new TagHelperInfo
            {
                TagName = tagName,
                Attributes = attributes,
                Line = lines.LineAt(element.Span.Start)
            });
        }

        return tagHelpers;
    }

    private static List<string> ExtractSections(RazorSyntaxNode root) =>
        root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .Where(d => GetDirectiveName(d) == "section")
            .Select(d => GetDirectiveBody(d)?.Split(new[] { ' ', '\t', '\r', '\n', '{' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault())
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()!;

    // Razor's internal SyntaxNode.ToString() is a debug string ("RazorDirective at 0::14");
    // ToFullString() returns the actual source text, like Roslyn.
    private static string? GetDirectiveName(RazorDirectiveSyntax? directive)
    {
        if (directive == null) return null;
        return directive.DirectiveDescriptor?.Directive
            ?? directive.ToFullString().Trim().TrimStart('@').Split(' ').FirstOrDefault();
    }

    private static string? GetDirectiveBody(RazorDirectiveSyntax? directive)
    {
        if (directive == null) return null;
        var text = directive.ToFullString().Trim();
        var spaceIdx = text.IndexOf(' ');
        return spaceIdx > 0 ? text[(spaceIdx + 1)..].Trim() : null;
    }

    private string GetRelativePath(string filePath)
    {
        var fullProjectRoot = Path.GetFullPath(_projectRoot);
        var fullFilePath = Path.GetFullPath(filePath);
        if (fullFilePath.StartsWith(fullProjectRoot, StringComparison.OrdinalIgnoreCase))
            return fullFilePath[fullProjectRoot.Length..].TrimStart('\\', '/');
        return filePath;
    }
}

public sealed class RazorPageInfo
{
    public required string Id { get; init; }
    public required string FilePath { get; init; }
    public required string RelativePath { get; init; }
    public bool IsPage { get; init; }
    public string? RouteTemplate { get; init; }
    public string? ModelType { get; init; }
    public string? Layout { get; init; }
    public List<string> InjectedServices { get; init; } = new();
    public List<string> ViewDataKeys { get; init; } = new();
    public List<PartialRenderInfo> Partials { get; init; } = new();
    public List<TagHelperInfo> TagHelpers { get; init; } = new();
    public List<string> Sections { get; init; } = new();
}

public sealed class PartialRenderInfo
{
    public string Name { get; set; } = string.Empty;
    public bool IsTagHelper { get; set; }
    public int Line { get; set; }
}

public sealed class TagHelperInfo
{
    public string TagName { get; set; } = string.Empty;
    public List<TagHelperAttributeInfo> Attributes { get; set; } = new();
    public int Line { get; set; }
}

public sealed class TagHelperAttributeInfo
{
    public string Name { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}
