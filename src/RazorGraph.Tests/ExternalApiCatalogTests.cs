namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using RazorGraph.Lua.ExternalApis;
using RazorGraph.Lua.Hosts;
using Xunit;

/// <summary>
/// The Lightroom Classic SDK catalogue: what it knows, what it refuses to claim,
/// and the validation that keeps file names from being promoted into it.
/// </summary>
public class ExternalApiCatalogTests
{
    private static ExternalApiCatalog Catalog => LightroomHost.Catalog;

    [Fact]
    public void Catalogue_LoadsFromTheEmbeddedResource()
    {
        Assert.Equal("lightroom-classic", Catalog.Name);
        Assert.Equal("classic", Catalog.Variant);
        Assert.NotEmpty(Catalog.Modules);
    }

    [Fact]
    public void Catalogue_ContainsOnlyNamesThatCouldBeImportTargets()
    {
        // The escalation this guards: module names are FILE NAMES from an
        // arbitrary caller-supplied directory, promoted into a catalogue the
        // analyzer then treats as authoritative. Unvalidated, six documentation
        // pages ("LrView child layout properties" and kin) were catalogued as
        // real SDK modules -- none of which `import` can name.
        var invalid = Catalog.Modules.Keys
            .Where(k => !System.Text.RegularExpressions.Regex.IsMatch(k, "^Lr[A-Za-z0-9]+$"))
            .ToList();

        Assert.Empty(invalid);
    }

    [Fact]
    public void Classify_KnownModule_ReportsWhereItWasCatalogued()
    {
        var verdict = Assert.IsType<ExternalApiVerdict.Known>(Catalog.Classify("LrDialogs"));

        Assert.Equal("LrDialogs", verdict.Name);
        Assert.Contains(verdict.FirstCataloguedIn, Catalog.CatalogedVersions);
        Assert.Null(verdict.AbsentAfter);
    }

    [Fact]
    public void Classify_ModuleAddedLater_IsDatedToTheLaterVersion()
    {
        // LrDevelopController is absent from SDK 3.0 and present in 8.0, so a
        // plug-in importing it cannot run on the older SDK.
        var verdict = Assert.IsType<ExternalApiVerdict.Known>(Catalog.Classify("LrDevelopController"));

        Assert.Equal("8.0", verdict.FirstCataloguedIn);
    }

    [Fact]
    public void Classify_RetiredModule_CarriesAbsentAfter()
    {
        // Present in 3.0, gone by 8.0 -- a real compatibility signal rather than trivia.
        var verdict = Assert.IsType<ExternalApiVerdict.Known>(Catalog.Classify("LrJournalProgressScope"));

        Assert.Equal("3.0", verdict.AbsentAfter);
    }

    [Fact]
    public void Classify_UnrecognisedModule_IsUnknownToTheCatalogueNotInvalid()
    {
        // Never "nonexistent": the catalogue only knows the SDKs someone
        // downloaded, so an unrecognised Lr* module may be real and simply
        // uncatalogued. Claiming otherwise is absence-as-finding wearing a
        // version number.
        var verdict = Assert.IsType<ExternalApiVerdict.UnknownToCatalogue>(Catalog.Classify("LrNoSuchModule"));

        Assert.Contains("not present in any catalogued version", verdict.Reason);
        Assert.All(Catalog.CatalogedVersions, v => Assert.Contains(v, verdict.Reason));
    }

    [Fact]
    public void Classify_UndocumentedModule_IsKnownToExistButNotDescribed()
    {
        // LrController and LrTableUtils are imported by Adobe's OWN sample
        // plug-ins and appear on no API Reference page; LrController is absent
        // from both SDK guides too, so the only evidence it exists is that
        // Adobe's code uses it. Calling it unknown would send someone hunting a
        // typo that is not there.
        var verdict = Assert.IsType<ExternalApiVerdict.KnownUndocumented>(Catalog.Classify("LrController"));

        Assert.Equal("LrController", verdict.Name);
        Assert.NotEmpty(verdict.Evidence);
    }

    [Fact]
    public void UndocumentedModules_AreNotAlsoCataloguedAsDocumented()
    {
        // The two lists must stay disjoint, or a module would report as both
        // documented and undocumented depending on lookup order.
        Assert.DoesNotContain(Catalog.UndocumentedModules.Keys, Catalog.Modules.ContainsKey);
    }

    [Fact]
    public void UndocumentedModules_CarryCheckableEvidence()
    {
        // Evidence, not assertion: each entry names the sample file it was
        // observed in, so the claim can be re-verified against the SDK.
        Assert.All(Catalog.UndocumentedModules.Values, m =>
        {
            Assert.NotEmpty(m.SeenIn);
            Assert.All(m.Evidence, e => Assert.Contains(".lua", e));
        });
    }

    // ---- Info.lua manifest keys -------------------------------------------

    [Fact]
    public void ManifestKeys_CarryTheRequiredOnesFromTheSpecification()
    {
        // Only the guide can say what is mandatory; the samples show usage, not
        // obligation. These two are the whole of what a manifest must declare.
        var required = Catalog.ManifestKeys.Where(k => k.Value.Required == true).Select(k => k.Key).OrderBy(k => k);

        Assert.Equal(["LrSdkVersion", "LrToolkitIdentifier"], required);
    }

    [Fact]
    public void ManifestKeys_RecoverNamesBrokenAcrossALineInTheGuide()
    {
        // Both were truncated at a hyphenated line break -- LrAlsoUseBuiltInTransla
        // and LrLimitNumberOfTempRend. Rejoining recovers them exactly, and getting
        // this wrong means shipping half a key name as if it were whole.
        Assert.True(Catalog.ManifestKeys.ContainsKey("LrAlsoUseBuiltInTranslations"));
        Assert.True(Catalog.ManifestKeys.ContainsKey("LrLimitNumberOfTempRenditions"));
    }

    [Fact]
    public void ManifestKeys_SplitRowsThatDocumentTwoKeysTogether()
    {
        // The guide documents LrLibraryMenuItems and LrHelpMenuItems in one table
        // row, which arrives concatenated. Left joined, neither key is findable.
        Assert.True(Catalog.ManifestKeys.ContainsKey("LrLibraryMenuItems"));
        Assert.True(Catalog.ManifestKeys.ContainsKey("LrHelpMenuItems"));
        Assert.DoesNotContain(Catalog.ManifestKeys.Keys, k => k.Contains("ItemsLr"));
    }

    [Fact]
    public void ManifestKeys_NameTheirSource()
    {
        // Confidence is not uniform: samples are exact but partial, the guide is
        // the specification. A reader must be able to tell which produced a key.
        Assert.All(Catalog.ManifestKeys.Values, k =>
            Assert.Contains(k.Source, new[] { "samples", "guide", "samples+guide" }));

        // Keys the examples never use are exactly why the guide is worth reading.
        Assert.Contains(Catalog.ManifestKeys, k => k.Value.Source == "guide");
    }

    [Fact]
    public void ManifestKeys_AreNotConfusedWithModules()
    {
        // Both vocabularies are Lr-prefixed and both come out of the same SDK, but
        // LrExportServiceProvider is a manifest key and LrDialogs is a module.
        // Cataloguing one as the other would make import resolution nonsense.
        Assert.DoesNotContain(Catalog.ManifestKeys.Keys, Catalog.Modules.ContainsKey);
    }

    [Fact]
    public void Classify_MentionsBeingOutOfDateOnlyWhenItIs()
    {
        // State-independent: the "may simply be newer" caveat must appear exactly
        // when the catalogue trails the newest known release, so that regenerating
        // it neither silences a real caveat nor leaves a stale one behind.
        var reason = Assert.IsType<ExternalApiVerdict.UnknownToCatalogue>(Catalog.Classify("LrNoSuchModule")).Reason;

        if (Catalog.IsBehindLatest) Assert.Contains("may simply be newer", reason);
        else Assert.DoesNotContain("may simply be newer", reason);
    }

    [Fact]
    public void Provenance_RecordsWhatEachSourcePackageSaidAboutItself()
    {
        // The control against the escalation that produced this field: a reader
        // must be able to tell whether the data came from Adobe or from a mirror.
        Assert.NotEmpty(Catalog.Provenance);
        Assert.All(Catalog.CatalogedVersions, v =>
        {
            Assert.True(Catalog.Provenance.ContainsKey(v), $"No provenance recorded for catalogued version {v}.");
            Assert.Contains("Software Development Kit", Catalog.Provenance[v].Declares);
            Assert.Contains(v, Catalog.Provenance[v].Declares);
        });
    }

    [Fact]
    public void MinimumVersion_IsTheLatestVersionAnyModuleRequires()
    {
        var minimum = Catalog.MinimumVersionFor(["LrDialogs", "LrDevelopController"]);

        Assert.Equal("8.0", minimum);
    }

    [Fact]
    public void MinimumVersion_IgnoresModulesItDoesNotKnow()
    {
        // An uncatalogued name must not silently drag the answer to the newest
        // version, nor block one being given for the modules that ARE known.
        Assert.Equal("3.0", Catalog.MinimumVersionFor(["LrDialogs", "LrNotAThing"]));
    }

    [Fact]
    public void Annotate_StampsMinimumVersionAndUnknownModules()
    {
        var host = new LightroomHost(Path.GetTempPath());
        var module = new GraphNode { Id = "mod:x", Type = NodeType.Unknown, ForeignType = "luaModule", Name = "x" };

        host.Annotate(module, ["LrDialogs", "LrDevelopController", "LrSomethingFromSdk14"]);

        Assert.Equal("8.0", module.GetProperty<string>("minimumSdkVersion"));
        Assert.Equal(["LrSomethingFromSdk14"], module.GetProperty<List<string>>("sdkModulesUnknownToCatalogue"));
        Assert.Contains("shipped through", module.GetProperty<string>("sdkCatalogueRange"));
    }

    [Fact]
    public void Annotate_IgnoresNonSdkExternals()
    {
        // A plugin's own unresolved sibling is not an SDK module and must not be
        // classified as one.
        var host = new LightroomHost(Path.GetTempPath());
        var module = new GraphNode { Id = "mod:x", Type = NodeType.Unknown, ForeignType = "luaModule", Name = "x" };

        host.Annotate(module, ["SomeHelper"]);

        Assert.Null(module.GetProperty<string>("minimumSdkVersion"));
        Assert.Null(module.GetProperty<List<string>>("sdkModulesUnknownToCatalogue"));
    }
}
