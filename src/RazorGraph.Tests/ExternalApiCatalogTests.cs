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
        // firstCataloguedIn describes OUR COLLECTION: absent from the local 3.0
        // package, present in the local 8.0 one. It is deliberately not the same
        // as firstSupportedIn, which Adobe states as 6.0 -- keeping both is what
        // makes the difference between the two visible.
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
    public void MinimumVersion_UsesAdobesVersionNotOurCollections()
    {
        // Adobe documents LrDevelopController as 6.0. The catalogue infers 8.0
        // only because that is the oldest local SDK containing it -- a fact about
        // this machine, not the product. Reporting 8.0 overstates what a plug-in
        // needs, which it did until the LuaDoc versions were read.
        Assert.Equal("6.0", Catalog.MinimumVersionFor(["LrDialogs", "LrDevelopController"]));
    }

    [Fact]
    public void MinimumVersion_IgnoresModulesItDoesNotKnow()
    {
        // An uncatalogued name must not silently drag the answer to the newest
        // version, nor block one being given for the modules that ARE known.
        Assert.Equal("1.3", Catalog.MinimumVersionFor(["LrDialogs", "LrNotAThing"]));
    }

    [Fact]
    public void MinimumVersion_ComparesVersionsRatherThanCataloguePositions()
    {
        // Vendor-stated versions are mostly releases this catalogue does not
        // hold, so position in catalogedVersions cannot order them. 1.3 vs 6.0
        // is a version comparison or it is nothing.
        Assert.Equal("6.0", Catalog.MinimumVersionFor(["LrApplication", "LrApplicationView"]));
    }

    // ---- Per-function detail ----------------------------------------------

    [Fact]
    public void Functions_CarryTheirOwnIntroductionVersion()
    {
        var dialogs = Catalog.Modules["LrDialogs"];

        Assert.NotNull(dialogs.Functions);
        Assert.Equal("1.3", dialogs.Functions!["message"].Since);

        // Same module, much later function -- the reason module-level versions
        // are not enough.
        Assert.Equal("5.0", dialogs.Functions["showBezel"].Since);
    }

    [Fact]
    public void Functions_CarryParameterNamesAndTypes()
    {
        var confirm = Catalog.Modules["LrDialogs"].Functions!["confirm"];

        Assert.NotNull(confirm.Params);
        Assert.Equal("message:string", confirm.Params![0]);
        Assert.Contains("optional", confirm.Params[1]);
    }

    [Fact]
    public void MinimumVersionForFunctions_IsHigherThanTheModuleWhenAFunctionIsNewer()
    {
        // Importing LrDialogs needs 1.3; calling showBezel needs 5.0. Answering
        // the module question when asked the function question understates it.
        Assert.Equal("1.3", Catalog.MinimumVersionFor(["LrDialogs"]));
        Assert.Equal("5.0", Catalog.MinimumVersionForFunctions("LrDialogs", ["message", "showBezel"]));
    }

    [Fact]
    public void MinimumVersionForFunctions_FallsBackToTheModuleForUnknownNames()
    {
        // An unrecognised function must not read as "no requirement" -- the
        // module still has to exist.
        Assert.Equal("1.3", Catalog.MinimumVersionForFunctions("LrDialogs", ["noSuchFunction"]));
    }

    [Fact]
    public void Annotate_StampsMinimumVersionAndUnknownModules()
    {
        var host = new LightroomHost(Path.GetTempPath());
        var module = new GraphNode { Id = "mod:x", Type = NodeType.Unknown, ForeignType = "luaModule", Name = "x" };

        host.Annotate(module, ["LrDialogs", "LrDevelopController", "LrSomethingFromSdk14"]);

        // 6.0, not 8.0: Adobe documents LrDevelopController as 6.0, and 8.0 was
        // only ever the oldest local SDK that happened to contain it.
        Assert.Equal("6.0", module.GetProperty<string>("minimumSdkVersion"));
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
