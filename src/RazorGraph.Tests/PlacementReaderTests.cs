namespace RazorGraph.Tests;

using RazorGraph.Extractor.Binding;
using Xunit;

/// <summary>
/// Reading OrchardCore placement.json. The cases are taken from the eight files
/// the framework itself ships, because those are what a parser has to survive.
/// </summary>
public class PlacementReaderTests
{
    private static IReadOnlyList<PlacementEntry> Parse(string text) =>
        PlacementReader.Parse(text, "placement.json");

    [Fact]
    public void CommentsAndTrailingCommas_AreSurvived()
    {
        // OrchardCore.Contents ships exactly this: a banner comment listing the
        // available shapes, and a block of //-commented example entries after the
        // last real one. A strict JSON parser fails on the framework's own file,
        // so the reader matches the runtime instead of the specification.
        var entries = Parse("""
            /*
              Available display shapes:
                  Parts_Contents_Publish
            */

            {
              "Parts_Contents_Publish": [
                {
                  "displayType": "Detail",
                  "place": "Content:5"
                }
              ]
              //,
              //"TextField_Edit": [
              //  { "place": "Content:60" }
              //]
            }
            /* trailing notes */
            """);

        var entry = Assert.Single(entries);
        Assert.Equal("Parts_Contents_Publish", entry.ShapeType);
        Assert.Equal("Content:5", entry.Place);
        Assert.Equal("Detail", entry.DisplayType);
    }

    [Fact]
    public void TheLine_IsTheEntryNotTheCommentThatNamesIt()
    {
        // The reason positions are read with Utf8JsonReader rather than found by
        // text search: Contents names Parts_Contents_Publish in its banner first,
        // and a finding pointing at a comment sends a reader to the wrong place.
        var entries = Parse("""
            /*
                Parts_Contents_Publish
            */
            {
              "Parts_Contents_Publish": [ { "place": "Content:5" } ]
            }
            """);

        Assert.Equal(5, Assert.Single(entries).Line);
    }

    [Fact]
    public void PlaceDash_HidesTheShape()
    {
        // OrchardCore.Seo drops three MediaField renders this way. A shape that
        // never renders cannot throw for want of a template.
        var entries = Parse("""
            {
              "MediaField": [
                { "place": "-", "differentiator": "SeoMetaPart-DefaultSocialImage" }
              ]
            }
            """);

        var entry = Assert.Single(entries);
        Assert.True(entry.Hides);

        // Conditional: it hides ONE differentiator, so every other render is live
        // and the rule must not retire a finding.
        Assert.False(entry.IsUnconditional);
    }

    [Fact]
    public void AnUnfilteredHide_IsUnconditional()
    {
        var entry = Assert.Single(Parse("""{ "ContentPreview_Button": [ { "place": "-" } ] }"""));

        Assert.True(entry.Hides);
        Assert.True(entry.IsUnconditional);
    }

    [Fact]
    public void AFilterKey_MakesAHideConditional()
    {
        // OrchardCore.Menu hides ContentPreview_Button for one content type only.
        // contentType is not a key this reader models, which is the point: an
        // unknown key is still a condition.
        var entry = Assert.Single(Parse("""
            { "ContentPreview_Button": [ { "place": "-", "contentType": [ "Menu" ] } ] }
            """));

        Assert.True(entry.Hides);
        Assert.False(entry.IsUnconditional);
        Assert.Equal(["contentType"], entry.Filters);
    }

    [Fact]
    public void AlternatesWrappersAndShape_AreRead()
    {
        var entry = Assert.Single(Parse("""
            {
              "TitlePart": [
                {
                  "place": "Header:5",
                  "alternates": [ "TitlePart_Fancy" ],
                  "wrappers": [ "TitlePart_Wrapper" ],
                  "shape": "TitlePart_Substitute"
                }
              ]
            }
            """));

        Assert.Equal(["TitlePart_Fancy"], entry.Alternates);
        Assert.Equal(["TitlePart_Wrapper"], entry.Wrappers);
        Assert.Equal("TitlePart_Substitute", entry.RenamedTo);

        // The modelled keys are not conditions, so the rule stays unconditional.
        Assert.Empty(entry.Filters);
    }

    [Fact]
    public void ASingleStringWhereAnArrayIsExpected_IsAccepted()
    {
        // Both spellings appear in the corpus for filter keys; the list keys are
        // read the same way rather than assuming the array and silently dropping
        // the name.
        var entry = Assert.Single(Parse("""
            { "TitlePart": [ { "alternates": "TitlePart_Fancy" } ] }
            """));

        Assert.Equal(["TitlePart_Fancy"], entry.Alternates);
    }

    [Fact]
    public void EveryRuleForOneShape_BecomesItsOwnEntry()
    {
        // Seo's MediaField carries three; each is a separate condition and a
        // separate finding candidate.
        var entries = Parse("""
            {
              "MediaField": [
                { "place": "-", "differentiator": "A" },
                { "place": "-", "differentiator": "B" },
                { "place": "Parts:20", "differentiator": "C" }
              ]
            }
            """);

        Assert.Equal(3, entries.Count);
        Assert.All(entries, e => Assert.Equal("MediaField", e.ShapeType));
        Assert.Equal(["A", "B", "C"], entries.Select(e => e.Differentiator));
    }

    [Fact]
    public void AFileThatIsNotAnObject_ReadsAsEmpty()
    {
        // Malformed config is a gap in the report, not a reason to have no graph.
        Assert.Empty(Parse("[]"));
    }
}
