namespace RazorGraph.Tests;

using RazorGraph.Extractor.Client;
using RazorGraph.Extractor.Razor;
using Xunit;

/// <summary>
/// Covers the client-side half of extraction: asset discovery under wwwroot, the
/// data-* keys a script reads, and the page-side scanners that say which assets a
/// page loads and which data-* keys it renders from server state.
/// </summary>
public class ClientAssetExtractorTests : IDisposable
{
    private readonly string _projectDir;

    public ClientAssetExtractorTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"rgtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_projectDir)) Directory.Delete(_projectDir, recursive: true);
        }
        catch (IOException)
        {
            // A leaked temp dir must not fail an otherwise passing run.
        }
        GC.SuppressFinalize(this);
    }

    private void WriteAsset(string relativePath, string content)
    {
        var full = Path.Combine(_projectDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    private static RazorPageInfo Extract(bool syntaxMode, string text) =>
        syntaxMode
            ? new RazorExtractor(Path.GetTempPath()).ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml")
            : new TextRazorExtractor().ExtractFromText(text, "test.cshtml", @"Pages\Test.cshtml");

    // ---- Asset discovery ---------------------------------------------------

    [Fact]
    public void ExtractAssets_FindsFirstPartyJsAndCss()
    {
        WriteAsset("wwwroot/js/site.js", "console.log('hi');");
        WriteAsset("wwwroot/css/site.css", "body { color: red; }");

        var assets = new ClientAssetExtractor().ExtractAssets(_projectDir);

        Assert.Equal(2, assets.Count);
        Assert.Contains(assets, a => a.Id == "js:wwwroot/js/site.js" && a.IsScript);
        Assert.Contains(assets, a => a.Id == "css:wwwroot/css/site.css" && !a.IsScript);
    }

    [Fact]
    public void ExtractAssets_SkipsVendorAndMinifiedBundles()
    {
        WriteAsset("wwwroot/js/app.js", "var a = 1;");
        WriteAsset("wwwroot/js/app.min.js", "var a=1");
        WriteAsset("wwwroot/lib/jquery/jquery.js", "/* vendor */");
        WriteAsset("wwwroot/lib/bootstrap/css/bootstrap.css", "/* vendor */");

        var assets = new ClientAssetExtractor().ExtractAssets(_projectDir);

        Assert.Single(assets);
        Assert.Equal("js:wwwroot/js/app.js", assets[0].Id);
    }

    [Fact]
    public void ExtractAssets_ReturnsEmpty_WhenNoWwwroot()
    {
        var assets = new ClientAssetExtractor().ExtractAssets(_projectDir);
        Assert.Empty(assets);
    }

    // ---- What a script reads ----------------------------------------------

    [Fact]
    public void ExtractAssets_CollectsDataKeys_FromEveryAccessForm()
    {
        WriteAsset("wwwroot/js/reader.js", """
            var a = frame.dataset.catalog;
            var b = el.dataset.fileName;
            var c = btn.getAttribute('data-filename');
            var d = document.querySelector('.item[data-state="Pick"]');
            """);

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        // dataset.fileName maps to data-file-name; the literal attribute in the
        // same file is data-filename. Both are real, distinct DOM keys.
        Assert.Contains("catalog", asset.DataKeys);
        Assert.Contains("file-name", asset.DataKeys);
        Assert.Contains("filename", asset.DataKeys);
        Assert.Contains("state", asset.DataKeys);
    }

    [Fact]
    public void ExtractAssets_CollectsLiteralApiCalls_AndIgnoresInterpolated()
    {
        WriteAsset("wwwroot/js/api.js", """
            fetch('/api/Comment/Upsert', { method: 'POST' });
            fetch(`/api/Status/Ping`);
            fetch(`/api/${name}/Dynamic`);
            $.post('/api/Legacy/Save', {});
            """);

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Contains("/api/Comment/Upsert", asset.ApiCalls);
        Assert.Contains("/api/Status/Ping", asset.ApiCalls);
        Assert.Contains("/api/Legacy/Save", asset.ApiCalls);
        Assert.DoesNotContain(asset.ApiCalls, u => u.Contains('$'));
    }

    [Fact]
    public void ExtractAssets_SeparatesWrittenKeysFromReadOnlyKeys()
    {
        WriteAsset("wwwroot/js/retry.js", """
            var attempt = parseInt(img.dataset.retryAttempt || '0');
            img.dataset.retryAttempt = attempt + 1;
            var catalog = frame.dataset.catalog;
            el.setAttribute('data-status', 'busy');
            """);

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Contains("retry-attempt", asset.DataKeysWritten);
        Assert.Contains("status", asset.DataKeysWritten);

        // A key the script assigns is state it owns; only 'catalog' has to come
        // from the server, so only it can be an unbound-key defect.
        Assert.Equal(new[] { "catalog" }, asset.DataKeysReadOnly.OrderBy(k => k).ToArray());
    }

    [Fact]
    public void ExtractAssets_TreatsComparisonAsRead_NotWrite()
    {
        WriteAsset("wwwroot/js/compare.js", "if (el.dataset.state === 'Pick') { go(); }");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Contains("state", asset.DataKeys);
        Assert.Empty(asset.DataKeysWritten);
    }

    [Fact]
    public void ExtractAssets_LeavesCssWithoutScriptAnalysis()
    {
        WriteAsset("wwwroot/css/theme.css", ".item[data-state='Pick'] { color: red; }");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.False(asset.IsScript);
        Assert.Empty(asset.DataKeys);
    }

    // ---- Path resolution ---------------------------------------------------

    [Theory]
    [InlineData("~/js/site.js", "wwwroot/js/site.js")]
    [InlineData("/js/site.js", "wwwroot/js/site.js")]
    [InlineData("~/css/site.css?v=abc123", "wwwroot/css/site.css")]
    [InlineData("~/js/site.js#frag", "wwwroot/js/site.js")]
    public void ResolveAssetPath_NormalizesToWwwrootRelative(string href, string expected)
    {
        Assert.Equal(expected, ClientAssetExtractor.ResolveAssetPath(href));
    }

    [Theory]
    [InlineData("https://cdn.example.com/x.js")]
    [InlineData("http://cdn.example.com/x.js")]
    [InlineData("//cdn.example.com/x.js")]
    [InlineData("")]
    public void ResolveAssetPath_ReturnsNull_ForExternalOrEmpty(string href)
    {
        Assert.Null(ClientAssetExtractor.ResolveAssetPath(href));
    }

    // ---- Page-side scanners (both extraction modes) ------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Page_CollectsScriptAndStylesheetReferences(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            <link rel="stylesheet" href="~/css/site.css" asp-append-version="true" />
            <script src="~/js/image-state.js" asp-append-version="true"></script>
            <script src="~/lib/jquery/dist/jquery.min.js"></script>
            """);

        Assert.Contains("~/css/site.css", info.AssetReferences);
        Assert.Contains("~/js/image-state.js", info.AssetReferences);
        Assert.Contains("~/lib/jquery/dist/jquery.min.js", info.AssetReferences);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Page_ServerDataKeys_RequireARazorExpression(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            <div id="frame" data-catalog="@Model.CatalogName" data-state="None">
              <span data-filename="@entry.FileName"></span>
            </div>
            """);

        // Server-computed values cross the boundary; a hardcoded literal does not.
        Assert.Contains("catalog", info.ServerDataKeys);
        Assert.Contains("filename", info.ServerDataKeys);
        Assert.DoesNotContain("state", info.ServerDataKeys);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Page_IgnoresAssetReferencesInsideRazorComments(bool syntaxMode)
    {
        var info = Extract(syntaxMode, """
            @* <script src="~/js/removed.js"></script> *@
            <script src="~/js/live.js"></script>
            """);

        Assert.Contains("~/js/live.js", info.AssetReferences);
        Assert.DoesNotContain("~/js/removed.js", info.AssetReferences);
    }
}
