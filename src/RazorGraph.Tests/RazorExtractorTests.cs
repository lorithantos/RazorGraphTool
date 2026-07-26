namespace RazorGraph.Tests;

using RazorGraph.Extractor.Razor;
using Xunit;

/// <summary>
/// Runs every extraction assertion against BOTH modes — the internal-syntax-API
/// path (RazorExtractor) and the regex fallback (TextRazorExtractor) — so the
/// fallback provably produces an equivalent RazorPageInfo.
/// </summary>
public class RazorExtractorTests
{
    private const string SamplePage = """
        @page "{id?}"
        @model IndexModel
        @inject IGreetingService Greetings
        @inject ILogger<IndexModel> Logger
        @{
            ViewData["Title"] = "Home";
            Layout = "_Layout";
        }
        <h1>@ViewData["Title"]</h1>
        <partial name="_Card" />
        @await Html.RenderPartialAsync("_Footer")
        <input asp-for="Name" class="form-control" />
        @section Scripts {
            <script></script>
        }
        """;

    private static RazorPageInfo Extract(bool syntaxMode, string text) =>
        syntaxMode
            ? new RazorExtractor(Path.GetTempPath()).ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml")
            : new TextRazorExtractor().ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_PageDirective_WithRoute(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.True(info.IsPage);
        Assert.Equal("{id?}", info.RouteTemplate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_PageDirective_WithoutRoute(bool syntaxMode)
    {
        var info = Extract(syntaxMode, "@page\n@model FooModel\n<p>hi</p>\n");

        Assert.True(info.IsPage);
        Assert.Null(info.RouteTemplate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_PageDirective_CrlfLineEndings(bool syntaxMode)
    {
        // Regression: bare @page in CRLF files — the runtime Razor pipeline NREs on a
        // null route (hence ProcessDesignTime), and $ in .NET multiline regex does not
        // match before \r (hence \r?$ in the fallback).
        var info = Extract(syntaxMode, "@page\r\n@model FooModel\r\n<p>hi</p>\r\n");

        Assert.True(info.IsPage);
        Assert.Null(info.RouteTemplate);
        Assert.Equal("FooModel", info.ModelType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NonPage_IsNotPage(bool syntaxMode)
    {
        var info = Extract(syntaxMode, "<div>partial content</div>\n");

        Assert.False(info.IsPage);
        Assert.Null(info.RouteTemplate);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_ModelDirective(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.Equal("IndexModel", info.ModelType);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_LayoutAssignment(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.Equal("_Layout", info.Layout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_LayoutDirective_BlazorForm(bool syntaxMode)
    {
        var info = Extract(syntaxMode, "@layout MainLayout\n<p>component</p>\n");

        Assert.Equal("MainLayout", info.Layout);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_InjectDirectives(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.Equal(2, info.InjectedServices.Count);
        Assert.Contains("IGreetingService Greetings", info.InjectedServices);
        Assert.Contains("ILogger<IndexModel> Logger", info.InjectedServices);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_ViewDataKeys_Deduplicated(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.Equal(new List<string> { "Title" }, info.ViewDataKeys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_Partials_WithLineNumbers(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        var card = Assert.Single(info.Partials, p => p.Name == "_Card");
        Assert.True(card.IsTagHelper);
        Assert.Equal(10, card.Line);

        var footer = Assert.Single(info.Partials, p => p.Name == "_Footer");
        Assert.False(footer.IsTagHelper);
        Assert.Equal(11, footer.Line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_HtmlPartialAsync(bool syntaxMode)
    {
        var info = Extract(syntaxMode, "<div>@await Html.PartialAsync(\"_Widget\")</div>\n");

        var widget = Assert.Single(info.Partials);
        Assert.Equal("_Widget", widget.Name);
        Assert.False(widget.IsTagHelper);
        Assert.Equal(1, widget.Line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_AspTagHelpers_WithAttributesAndLine(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        var input = Assert.Single(info.TagHelpers, t => t.TagName == "input");
        Assert.Equal(12, input.Line);
        Assert.Contains(input.Attributes, a => a.Name == "asp-for" && a.Value == "Name");
        Assert.Contains(input.Attributes, a => a.Name == "class" && a.Value == "form-control");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Ignores_ElementsWithoutAspAttributes(bool syntaxMode)
    {
        var info = Extract(syntaxMode, "<input type=\"text\" class=\"plain\" />\n");

        Assert.Empty(info.TagHelpers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Extracts_Sections(bool syntaxMode)
    {
        var info = Extract(syntaxMode, SamplePage);

        Assert.Equal(new List<string> { "Scripts" }, info.Sections);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Comments_AreIgnored_AndDoNotShiftLines(bool syntaxMode)
    {
        var text = """
            @* ViewData["Ghost"] should not
               be extracted from this comment,
               nor <partial name="_Ghost" /> *@
            <h2>@ViewData["Real"]</h2>
            <partial name="_Real" />
            """;

        var info = Extract(syntaxMode, text);

        Assert.Equal(new List<string> { "Real" }, info.ViewDataKeys);
        var real = Assert.Single(info.Partials);
        Assert.Equal("_Real", real.Name);
        Assert.Equal(5, real.Line);
    }

    [Fact]
    public void SetTagHelpers_Null_FallsBackToTextAnalysis_SameResult()
    {
        // Force the syntax path to throw so the facade drops to TextRazorExtractor.
        var extractor = new RazorExtractor(Path.GetTempPath());
        extractor.SetTagHelpers(null!);

        var viaFacade = extractor.ExtractFromText(SamplePage, "test.cshtml", @"Pages\Test.cshtml");
        var viaText = new TextRazorExtractor().ExtractFromText(SamplePage, "test.cshtml", @"Pages\Test.cshtml");

        Assert.Equal(viaText.IsPage, viaFacade.IsPage);
        Assert.Equal(viaText.ModelType, viaFacade.ModelType);
        Assert.Equal(viaText.ViewDataKeys, viaFacade.ViewDataKeys);
        Assert.Equal(viaText.Partials.Count, viaFacade.Partials.Count);
    }

    [Fact]
    public void BothModes_ProduceEquivalentPageInfo()
    {
        var syntax = Extract(syntaxMode: true, SamplePage);
        var text = Extract(syntaxMode: false, SamplePage);

        Assert.Equal(text.IsPage, syntax.IsPage);
        Assert.Equal(text.RouteTemplate, syntax.RouteTemplate);
        Assert.Equal(text.ModelType, syntax.ModelType);
        Assert.Equal(text.Layout, syntax.Layout);
        Assert.Equal(text.InjectedServices, syntax.InjectedServices);
        Assert.Equal(text.ViewDataKeys, syntax.ViewDataKeys);
        Assert.Equal(text.Sections, syntax.Sections);
        Assert.Equal(
            text.Partials.Select(p => (p.Name, p.IsTagHelper, p.Line)),
            syntax.Partials.Select(p => (p.Name, p.IsTagHelper, p.Line)));
        Assert.Equal(
            text.TagHelpers.Select(t => (t.TagName, t.Line)),
            syntax.TagHelpers.Select(t => (t.TagName, t.Line)));
    }
}
