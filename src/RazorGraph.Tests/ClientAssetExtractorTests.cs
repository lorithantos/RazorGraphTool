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

    // ---- Selector extraction -------------------------------------------------

    [Fact]
    public void Selectors_CollectsLiteralIds_FromAllThreeApis()
    {
        WriteAsset("wwwroot/js/cart.js", @"
            const total = document.getElementById('cart-total');
            document.querySelectorAll('#cart-list .item');
            $('#checkout-button').on('click', go);");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Equal(new[] { "cart-list", "cart-total", "checkout-button" },
            asset.SelectorIds.OrderBy(i => i));
        Assert.Equal(0, asset.DynamicSelectorCount);
    }

    [Fact]
    public void Selectors_CountsDynamicCallSites_InsteadOfGuessing()
    {
        WriteAsset("wwwroot/js/rows.js", @"
            document.getElementById('summary');
            document.getElementById('row-' + index);
            document.querySelector(activeSelector);");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Equal(new[] { "summary" }, asset.SelectorIds.ToArray());
        // Two of three call sites were computed; the graph must admit that.
        Assert.Equal(2, asset.DynamicSelectorCount);
    }

    [Fact]
    public void Selectors_SelfCreatedIds_AreNotAServerContract()
    {
        WriteAsset("wwwroot/js/popup.js", @"
            const el = document.createElement('div');
            el.id = 'popup-host';
            container.insertAdjacentHTML('beforeend', '<div id=""popup-body""></div>');
            document.getElementById('popup-host');
            document.getElementById('popup-body');
            document.getElementById('server-rendered');");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Equal(new[] { "popup-body", "popup-host" }, asset.OwnIds.OrderBy(i => i));
        Assert.Equal(new[] { "server-rendered" }, asset.SelectorIdsForeign.ToArray());
    }

    [Fact]
    public void Selectors_ClassTokens_AreIgnored()
    {
        // Utility classes are shared with every framework stylesheet; collecting
        // them would make the contract meaningless.
        WriteAsset("wwwroot/js/style.js", "$('.btn.active').hide(); document.querySelector('.card');");

        var asset = new ClientAssetExtractor().ExtractAssets(_projectDir).Single();

        Assert.Empty(asset.SelectorIds);
    }

    // ---- Vendor detection ----------------------------------------------------

    [Fact]
    public void VendorDirNames_MatchWholeSegments_NotSubstrings()
    {
        // The original rule substring-matched "\lib\", which admitted nopCommerce's
        // lib_npm — 102k LOC of vendor code — as first-party.
        WriteAsset("wwwroot/lib_npm/moment/moment.js", "/* vendor */");
        WriteAsset("wwwroot/js/site.js", "var a = 1;");
        // ...and the fix must not overshoot: a first-party dir merely *containing*
        // a vendor name is still first-party.
        WriteAsset("wwwroot/library-ui/panel.js", "var b = 2;");

        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(_projectDir);

        Assert.Equal(2, assets.Count);
        Assert.DoesNotContain(assets, a => a.Id.Contains("lib_npm"));
        Assert.Contains(assets, a => a.Id == "js:wwwroot/library-ui/panel.js");
        var skip = Assert.Single(extractor.LastSkipped);
        Assert.Equal("wwwroot/lib_npm/moment/moment.js", skip.RelativePath);
        Assert.Contains("lib_npm", skip.Reason);
    }

    [Fact]
    public void NpmScopedDirectory_IsVendor_WhateverItsParentIsCalled()
    {
        // Only an npm copy produces a directory literally named "@scope", so the
        // parent needs no recognizable name at all.
        WriteAsset("wwwroot/client_pkgs/@fortawesome/fontawesome.js", "/* vendor */");

        var extractor = new ClientAssetExtractor();

        Assert.Empty(extractor.ExtractAssets(_projectDir));
        Assert.Contains("@fortawesome", Assert.Single(extractor.LastSkipped).Reason);
    }

    [Fact]
    public void ShippedPackageManifest_MarksItsDirectoryVendor()
    {
        // Nobody hand-writes a package.json inside wwwroot; it came with a copy.
        WriteAsset("wwwroot/widgets/package.json", "{ \"name\": \"widgets\" }");
        WriteAsset("wwwroot/widgets/widget.js", "/* vendor */");
        WriteAsset("wwwroot/js/site.js", "var a = 1;");

        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(_projectDir);

        Assert.Single(assets);
        Assert.Equal("js:wwwroot/js/site.js", assets[0].Id);
        Assert.Contains("widgets", Assert.Single(extractor.LastSkipped).Reason);
    }

    [Fact]
    public void DirsMatchingRootDependencies_AreAPackageDrop()
    {
        // The nopCommerce shape: a copy task fills a wwwroot directory from
        // node_modules and leaves no manifest behind. The evidence is the root
        // package.json — the drop's children are named after its dependencies.
        WriteAsset("package.json", "{ \"dependencies\": { \"bootstrap\": \"^5.0.0\", \"moment\": \"^2.29.0\" } }");
        WriteAsset("wwwroot/ClientLibs/bootstrap/bootstrap.js", "/* vendor */");
        WriteAsset("wwwroot/ClientLibs/moment/moment.js", "/* vendor */");
        WriteAsset("wwwroot/js/site.js", "var a = 1;");

        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(_projectDir);

        Assert.Single(assets);
        Assert.Equal("js:wwwroot/js/site.js", assets[0].Id);
        Assert.Equal(2, extractor.LastSkipped.Count);
        Assert.All(extractor.LastSkipped, s => Assert.Contains("ClientLibs", s.Reason));
    }

    [Fact]
    public void ManifestInABuildAssetDirectory_CountsAsEvidence()
    {
        // The OrchardCore shape: each module keeps its package.json in Assets\,
        // and the build copies the packages into wwwroot. A root-only manifest
        // search misses it entirely.
        WriteAsset("Assets/package.json", "{ \"devDependencies\": { \"codemirror\": \"^5\", \"trumbowyg\": \"^2\" } }");
        WriteAsset("wwwroot/Scripts/codemirror/codemirror.js", "/* vendor */");
        WriteAsset("wwwroot/Scripts/trumbowyg/trumbowyg.js", "/* vendor */");
        WriteAsset("wwwroot/Scripts/bootstrap.js", "/* vendor, loose in the drop root */");

        var extractor = new ClientAssetExtractor();

        Assert.Empty(extractor.ExtractAssets(_projectDir));
        Assert.Equal(3, extractor.LastSkipped.Count);
        Assert.All(extractor.LastSkipped, s => Assert.Contains("Scripts", s.Reason));
    }

    [Fact]
    public void OneSharedDependencyName_IsNotEnoughEvidence()
    {
        // One shared name is coincidence, two is a copy task. "chart" here is a
        // first-party dir that happens to collide with a dependency name.
        WriteAsset("package.json", "{ \"dependencies\": { \"chart\": \"^1.0.0\", \"moment\": \"^2.29.0\" } }");
        WriteAsset("wwwroot/features/chart/chart.js", "var a = 1;");
        WriteAsset("wwwroot/features/orders/orders.js", "var b = 2;");

        var extractor = new ClientAssetExtractor();

        Assert.Equal(2, extractor.ExtractAssets(_projectDir).Count);
        Assert.Empty(extractor.LastSkipped);
    }

    [Fact]
    public void IncludeVendor_KeepsEverything_ClassifiedAndScanned()
    {
        WriteAsset("wwwroot/lib_npm/widget/widget.js", "el.dataset.userId = '1';");
        WriteAsset("wwwroot/js/app.min.js", "var a=1");
        WriteAsset("wwwroot/js/site.js", "var a = 1;");

        var extractor = new ClientAssetExtractor();
        var assets = extractor.ExtractAssets(_projectDir, includeVendor: true);

        Assert.Equal(3, assets.Count);
        Assert.Empty(extractor.LastSkipped);

        var vendor = assets.Single(a => a.Id == "js:wwwroot/lib_npm/widget/widget.js");
        Assert.True(vendor.IsVendor);
        Assert.Contains("lib_npm", vendor.VendorReason);
        // The point of including vendor code is analyzing it, so it gets the
        // same content scan as first-party code.
        Assert.Contains("user-id", vendor.DataKeysWritten);

        Assert.True(assets.Single(a => a.Id == "js:wwwroot/js/app.min.js").IsVendor);
        Assert.False(assets.Single(a => a.Id == "js:wwwroot/js/site.js").IsVendor);
    }

    [Fact]
    public void BuildOutput_IsNeverAnAsset_EvenWithIncludeVendor()
    {
        WriteAsset("wwwroot/obj/generated.js", "var a = 1;");

        var extractor = new ClientAssetExtractor();

        Assert.Empty(extractor.ExtractAssets(_projectDir, includeVendor: true));
        // Not vendor, not skipped — build output is not source at all.
        Assert.Empty(extractor.LastSkipped);
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
