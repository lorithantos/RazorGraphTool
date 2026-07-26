namespace RazorGraph.Extractor.Razor;

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Razor.Language;
using Microsoft.AspNetCore.Razor.Language.Syntax;
using RazorGraph.Core.Graph;

/// <summary>
/// Extracts Razor-specific structure from .cshtml and .razor files
/// using the official Razor parser.
/// </summary>
public sealed class RazorExtractor
{
    private readonly RazorProjectEngine _engine;
    private readonly string _projectRoot;

    public RazorExtractor(string projectRoot)
    {
        _projectRoot = projectRoot;
        var fileSystem = RazorProjectFileSystem.Create(projectRoot);
        _engine = RazorProjectEngine.Create(RazorConfiguration.Default, fileSystem);
    }

    public RazorPageInfo ExtractPage(string filePath)
    {
        var relativePath = GetRelativePath(filePath);
        var projectItem = _engine.FileSystem.GetItem(relativePath, "legacy");
        var codeDocument = _engine.Process(projectItem);
        var syntaxTree = codeDocument.GetSyntaxTree();
        var root = syntaxTree.Root;

        var info = new RazorPageInfo
        {
            Id = $"page:{relativePath}",
            FilePath = filePath,
            RelativePath = relativePath,
            IsPage = IsRazorPage(root),
            RouteTemplate = ExtractRouteTemplate(root),
            ModelType = ExtractModelDirective(root),
            Layout = ExtractLayout(root),
            InjectedServices = ExtractInjectDirectives(root),
            ViewDataKeys = ExtractViewDataReads(root),
            Partials = ExtractPartialRenders(root),
            TagHelpers = ExtractTagHelperInvocations(root),
            Sections = ExtractSections(root)
        };

        return info;
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

        return GetDirectiveBody(pageDir);
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

    private static List<string> ExtractViewDataReads(RazorSyntaxNode root)
    {
        var keys = new List<string>();
        var pattern = new Regex(@"ViewData\[(?:\u0022|\u0027)([^\u0022\u0027]+)(?:\u0022|\u0027)\]", RegexOptions.Compiled);

        var allText = root.ToString();
        foreach (Match m in pattern.Matches(allText))
            keys.Add(m.Groups[1].Value);

        return keys.Distinct().ToList();
    }

    private static List<PartialRenderInfo> ExtractPartialRenders(RazorSyntaxNode root)
    {
        var partials = new List<PartialRenderInfo>();
        var text = root.ToString();

        // Match <partial name="_Name" /> (Tag Helper syntax)
        var tagHelperRegex = new Regex(@"<partial\s+name\s*=\s*(?:\u0022|\u0027)([^\u0022\u0027]+)(?:\u0022|\u0027)", RegexOptions.Compiled);
        foreach (Match m in tagHelperRegex.Matches(text))
        {
            partials.Add(new PartialRenderInfo
            {
                Name = m.Groups[1].Value,
                IsTagHelper = true,
                Line = 0
            });
        }

        // Match @await Html.RenderPartialAsync("_Name" ...)
        var renderPartialRegex = new Regex(@"RenderPartialAsync\s*\(\s*(?:\u0022|\u0027)([^\u0022\u0027]+)(?:\u0022|\u0027)", RegexOptions.Compiled);
        foreach (Match m in renderPartialRegex.Matches(text))
        {
            partials.Add(new PartialRenderInfo
            {
                Name = m.Groups[1].Value,
                IsTagHelper = false,
                Line = 0
            });
        }

        return partials;
    }

    private static List<TagHelperInfo> ExtractTagHelperInvocations(RazorSyntaxNode root)
    {
        var tagHelpers = new List<TagHelperInfo>();

        foreach (var element in root.DescendantNodes().OfType<MarkupTagHelperElementSyntax>())
        {
            var tagName = element.StartTag?.Name?.ToString()?.Trim() ?? "unknown";
            var attributes = element.StartTag?.Attributes
                .OfType<MarkupTagHelperAttributeSyntax>()
                .Select(a => new TagHelperAttributeInfo
                {
                    Name = a.Name?.ToString()?.Trim() ?? "",
                    Value = a.Value?.ToString()?.Trim() ?? ""
                })
                .ToList() ?? new List<TagHelperAttributeInfo>();

            tagHelpers.Add(new TagHelperInfo
            {
                TagName = tagName,
                Attributes = attributes,
                Line = element.Span.Start.LineIndex
            });
        }

        return tagHelpers;
    }

    private static List<string> ExtractSections(RazorSyntaxNode root) =>
        root.DescendantNodes()
            .OfType<RazorDirectiveSyntax>()
            .Where(d => GetDirectiveName(d) == "section")
            .Select(d => GetDirectiveBody(d))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .ToList()!;

    private static string? GetDirectiveName(RazorDirectiveSyntax? directive)
    {
        if (directive == null) return null;
        return directive.DirectiveDescriptor?.Directive
            ?? directive.ToString()?.TrimStart('@').Split(' ').FirstOrDefault();
    }

    private static string? GetDirectiveBody(RazorDirectiveSyntax? directive)
    {
        if (directive == null) return null;
        var text = directive.ToString()?.Trim();
        if (text == null) return null;
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
