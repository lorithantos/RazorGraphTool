namespace RazorGraph.Tests;

using RazorGraph.Extractor.Binding;
using Xunit;

/// <summary>
/// The OrchardCore shape-name grammar. Every case here is a real name measured in
/// the corpus, not an invented one.
/// </summary>
public class ShapeNameGrammarTests
{
    private static string First(string shapeName) =>
        ShapeNameGrammar.CandidateTemplateNames(shapeName)[0];

    [Fact]
    public void DisplayTypeAndAlternate_AreReordered_NotSubstituted()
    {
        // The whole point. "replace __ with - then _ with ." yields
        // AuditTrailAdminFilters.Thumbnail-Category, which does not exist;
        // the real file puts the alternate before the display type.
        Assert.Equal("AuditTrailAdminFilters-Category.Thumbnail",
            First("AuditTrailAdminFilters_Thumbnail__Category"));

        Assert.Equal("CommonPart-Owner.Edit", First("CommonPart_Edit__Owner"));
    }

    [Fact]
    public void AlternateAlone_BecomesAHyphen()
    {
        // Straight from OrchardCore's debug output:
        //   bindings:Menu__Main => Views/Menu-Main.cshtml
        Assert.Equal("Menu-Main", First("Menu__Main"));
    }

    [Fact]
    public void DisplayTypeAlone_BecomesADot()
    {
        Assert.Equal("AuthenticatorAppLoginSettings.Edit", First("AuthenticatorAppLoginSettings_Edit"));
    }

    [Fact]
    public void PlainName_IsItsOwnTemplate()
    {
        Assert.Equal("CustomUserSettings", First("CustomUserSettings"));
    }

    [Fact]
    public void ChainedAlternates_AllAttachToTheBase()
    {
        // ShortcodeDescriptor_SummaryAdmin__Button__Actions is real and was one of
        // the four names the first measurement could not resolve.
        Assert.Equal("ShortcodeDescriptor-Button-Actions.SummaryAdmin",
            First("ShortcodeDescriptor_SummaryAdmin__Button__Actions"));
    }

    [Fact]
    public void FallbackOrder_DropsAlternatesBeforeDisplayTypes()
    {
        // OrchardCore resolves "alternates from highest to lowest priority, then
        // the shape type and its fallback types", so a missing specific template
        // must fall back to the less specific one rather than to nothing.
        var candidates = ShapeNameGrammar.CandidateTemplateNames("Foo_Edit__Bar").ToList();

        Assert.Equal("Foo-Bar.Edit", candidates[0]);
        Assert.Contains("Foo.Edit", candidates);
        Assert.Contains("Foo", candidates);
        Assert.True(candidates.IndexOf("Foo.Edit") < candidates.IndexOf("Foo"));
    }

    [Fact]
    public void CandidatesAreDistinct()
    {
        // A plain name generates the same string several ways; duplicates would
        // make a caller think it had several bindings.
        var candidates = ShapeNameGrammar.CandidateTemplateNames("Simple");

        Assert.Equal(candidates.Distinct(StringComparer.OrdinalIgnoreCase).Count(), candidates.Count);
    }

    [Fact]
    public void EmptyName_YieldsNothing()
    {
        Assert.Empty(ShapeNameGrammar.CandidateTemplateNames(""));
        Assert.Empty(ShapeNameGrammar.CandidateTemplateNames("   "));
    }
}
