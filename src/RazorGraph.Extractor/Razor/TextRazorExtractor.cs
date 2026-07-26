namespace RazorGraph.Extractor.Razor;

using System.Text.RegularExpressions;

/// <summary>
/// Regex-based Razor extraction producing the same RazorPageInfo contract as
/// RazorExtractor. Serves as the fallback when the internal Razor syntax API
/// throws; heuristic by design, adequate for graph construction.
/// </summary>
public sealed class TextRazorExtractor
{
    // \r?$ — in .NET multiline mode $ matches before \n only, so CRLF files need the \r allowed.
    private static readonly Regex PageDirectiveRegex = new(
        @"^\s*@page(?:[ \t]+(?:""|')(?<route>[^""']*)(?:""|'))?[ \t]*\r?$",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex ModelDirectiveRegex = new(
        @"^\s*@model[ \t]+(?<t>[^\r\n]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex InjectDirectiveRegex = new(
        @"^\s*@inject[ \t]+(?<b>[^\r\n]+)",
        RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex SectionDirectiveRegex = new(
        @"@section[ \t]+(?<n>\w+)",
        RegexOptions.Compiled);

    public RazorPageInfo ExtractFromText(string text, string filePath, string relativePath)
    {
        var stripped = RazorTextScanners.StripRazorComments(text);
        var lines = new LineMap(stripped);

        var pageMatch = PageDirectiveRegex.Match(stripped);

        return new RazorPageInfo
        {
            Id = $"page:{relativePath}",
            FilePath = filePath,
            RelativePath = relativePath,
            IsPage = pageMatch.Success,
            RouteTemplate = pageMatch.Success && pageMatch.Groups["route"].Success
                ? pageMatch.Groups["route"].Value
                : null,
            ModelType = ModelDirectiveRegex.Match(stripped) is { Success: true } m ? m.Groups["t"].Value.Trim() : null,
            Layout = RazorTextScanners.ScanLayoutDirective(stripped)
                ?? RazorTextScanners.ScanLayoutAssignment(stripped),
            InjectedServices = InjectDirectiveRegex.Matches(stripped)
                .Select(x => x.Groups["b"].Value.Trim())
                .Where(s => s.Length > 0)
                .ToList(),
            ViewDataKeys = RazorTextScanners.ScanViewDataKeys(stripped),
            Partials = RazorTextScanners.ScanPartialRenders(stripped, lines),
            TagHelpers = RazorTextScanners.ScanAspTagHelpers(stripped, lines),
            Sections = SectionDirectiveRegex.Matches(stripped)
                .Select(x => x.Groups["n"].Value)
                .Distinct()
                .ToList()
        };
    }
}

/// <summary>
/// Shared regex scanners used by both extraction modes for constructs that are
/// text-shaped even in the syntax path (ViewData keys, partial renders, asp-* tags).
/// </summary>
internal static class RazorTextScanners
{
    private static readonly Regex RazorCommentRegex = new(
        @"@\*.*?\*@", RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ViewDataRegex = new(
        @"ViewData\[(?:""|')([^""']+)(?:""|')\]", RegexOptions.Compiled);

    private static readonly Regex PartialTagRegex = new(
        @"<partial\s+name\s*=\s*(?:""|')([^""']+)(?:""|')", RegexOptions.Compiled);

    private static readonly Regex RenderPartialRegex = new(
        @"RenderPartialAsync\s*\(\s*(?:""|')([^""']+)(?:""|')", RegexOptions.Compiled);

    private static readonly Regex PartialAsyncRegex = new(
        @"Html\.PartialAsync\s*\(\s*(?:""|')([^""']+)(?:""|')", RegexOptions.Compiled);

    private static readonly Regex LayoutAssignmentRegex = new(
        @"Layout\s*=\s*(?:""|')([^""']+)(?:""|')", RegexOptions.Compiled);

    private static readonly Regex LayoutDirectiveRegex = new(
        @"^\s*@layout[ \t]+(?<l>\S+)", RegexOptions.Multiline | RegexOptions.Compiled);

    private static readonly Regex TagWithAttrsRegex = new(
        @"<(?<tag>[a-zA-Z][\w-]*)(?<attrs>(?:\s+[\w-]+(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?)*)\s*/?>",
        RegexOptions.Compiled);

    private static readonly Regex AttrRegex = new(
        @"(?<name>[\w-]+)\s*=\s*(?:""(?<val>[^""]*)""|'(?<val>[^']*)')", RegexOptions.Compiled);

    /// <summary>
    /// Replaces @* ... *@ comments with an equivalent number of newlines so that
    /// line numbers computed from the stripped text still match the original file.
    /// </summary>
    public static string StripRazorComments(string text) =>
        RazorCommentRegex.Replace(text, m => new string('\n', m.Value.Count(c => c == '\n')));

    public static List<string> ScanViewDataKeys(string text) =>
        ViewDataRegex.Matches(text).Select(m => m.Groups[1].Value).Distinct().ToList();

    public static string? ScanLayoutAssignment(string text) =>
        LayoutAssignmentRegex.Match(text) is { Success: true } m ? m.Groups[1].Value : null;

    public static string? ScanLayoutDirective(string text) =>
        LayoutDirectiveRegex.Match(text) is { Success: true } m ? m.Groups["l"].Value.Trim('"', '\'') : null;

    public static List<PartialRenderInfo> ScanPartialRenders(string text, LineMap lines)
    {
        var partials = new List<PartialRenderInfo>();

        foreach (Match m in PartialTagRegex.Matches(text))
        {
            partials.Add(new PartialRenderInfo
            {
                Name = m.Groups[1].Value,
                IsTagHelper = true,
                Line = lines.LineAt(m.Index)
            });
        }

        foreach (Match m in RenderPartialRegex.Matches(text).Concat(PartialAsyncRegex.Matches(text)))
        {
            partials.Add(new PartialRenderInfo
            {
                Name = m.Groups[1].Value,
                IsTagHelper = false,
                Line = lines.LineAt(m.Index)
            });
        }

        return partials;
    }

    public static List<TagHelperInfo> ScanAspTagHelpers(string text, LineMap lines)
    {
        var tagHelpers = new List<TagHelperInfo>();

        foreach (Match m in TagWithAttrsRegex.Matches(text))
        {
            var attributes = AttrRegex.Matches(m.Groups["attrs"].Value)
                .Select(a => new TagHelperAttributeInfo
                {
                    Name = a.Groups["name"].Value,
                    Value = a.Groups["val"].Value
                })
                .ToList();

            if (attributes.Any(a => a.Name.StartsWith("asp-", StringComparison.OrdinalIgnoreCase)))
            {
                tagHelpers.Add(new TagHelperInfo
                {
                    TagName = m.Groups["tag"].Value,
                    Attributes = attributes,
                    Line = lines.LineAt(m.Index)
                });
            }
        }

        return tagHelpers;
    }
}

/// <summary>
/// Precomputed newline offsets for converting character offsets to 1-based line numbers.
/// </summary>
internal sealed class LineMap
{
    private readonly int[] _lineStarts;

    public LineMap(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n') starts.Add(i + 1);
        }
        _lineStarts = starts.ToArray();
    }

    public int LineAt(int offset)
    {
        var idx = Array.BinarySearch(_lineStarts, offset);
        if (idx < 0) idx = ~idx - 1;
        return idx + 1;
    }
}
