namespace RazorGraph.Tests;

using RazorGraph.Extractor.Client;
using RazorGraph.Extractor.Razor;
using Xunit;

/// <summary>
/// Script blocks authored inside a .cshtml. They were the blind spot in client
/// extraction: an asset scan of wwwroot cannot see them, so any data-key or API
/// coupling created inline was invisible regardless of how obvious it was.
/// </summary>
public class InlineScriptExtractionTests
{
    private static RazorPageInfo Extract(bool syntaxMode, string text) =>
        syntaxMode
            ? new RazorExtractor(Path.GetTempPath()).ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml")
            : new TextRazorExtractor().ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml");

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_FindsBlockWithoutSrc(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            <div id="x"></div>
            <script>
                const el = document.getElementById('x');
            </script>
            """);

        Assert.Single(info.InlineScripts);
        Assert.Contains("getElementById", info.InlineScripts[0].Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_IgnoresExternalReferences(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            <script src="~/js/site.js"></script>
            <script src="~/js/other.js" asp-append-version="true"></script>
            """);

        Assert.Empty(info.InlineScripts);
        // Those are still asset references — the two paths must not double-count.
        Assert.Equal(2, info.AssetReferences.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_IgnoresEmptyBlocks(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            <script></script>
            <script>
            </script>
            """);

        Assert.Empty(info.InlineScripts);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_ReportsOpeningLine(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            <div></div>
            <script>
                var a = 1;
            </script>
            """);

        Assert.Single(info.InlineScripts);
        Assert.Equal(3, info.InlineScripts[0].Line);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_SkipsRazorComments(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            @* <script>var commented = 1;</script> *@
            <script>var real = 2;</script>
            """);

        Assert.Single(info.InlineScripts);
        Assert.Contains("real", info.InlineScripts[0].Body);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ScanInlineScripts_FindsMultipleBlocks(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @page
            <script>var a = 1;</script>
            <script src="~/js/site.js"></script>
            <script>var b = 2;</script>
            """);

        Assert.Equal(2, info.InlineScripts.Count);
    }

    // ---- The inline block as a graph asset ----------------------------------

    [Fact]
    public void BuildInlineScript_RunsTheSameScannersAsAFile()
    {
        var asset = ClientAssetExtractor.BuildInlineScript(
            idScope: null,
            pageRelativePath: @"Pages\Catalog.cshtml",
            pageFilePath: @"D:\app\Pages\Catalog.cshtml",
            body: """
                const el = document.getElementById('c');
                const n = el.dataset.catalogName;
                el.dataset.clientOwned = '1';
                fetch('/api/catalogs').then(r => r.json());
                """,
            line: 42,
            lineCount: 4);

        Assert.True(asset.IsScript);
        Assert.True(asset.IsInline);
        Assert.Equal(42, asset.LineStart);
        Assert.Contains("catalog-name", asset.DataKeys);
        Assert.Contains("client-owned", asset.DataKeysWritten);
        // A key the script writes is client-owned state, not something the server owes.
        Assert.DoesNotContain("client-owned", asset.DataKeysReadOnly);
        Assert.Contains("/api/catalogs", asset.ApiCalls);
    }

    [Fact]
    public void BuildInlineScript_IdIsStableAndPointsAtThePage()
    {
        var asset = ClientAssetExtractor.BuildInlineScript(
            null, @"Pages\Catalog.cshtml", @"D:\app\Pages\Catalog.cshtml", "var a = 1;", 7, 1);

        Assert.Equal("js:Pages/Catalog.cshtml#inline-7", asset.Id);
        Assert.Equal(@"D:\app\Pages\Catalog.cshtml", asset.FilePath);
    }

    [Fact]
    public void BuildInlineScript_HonoursIdScope()
    {
        var asset = ClientAssetExtractor.BuildInlineScript(
            "SampleWeb", @"Pages\Catalog.cshtml", @"D:\app\Pages\Catalog.cshtml", "var a = 1;", 7, 1);

        Assert.Equal("js:SampleWeb/Pages/Catalog.cshtml#inline-7", asset.Id);
    }
}
