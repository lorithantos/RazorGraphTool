namespace RazorGraph.Tests;

using RazorGraph.Extractor.Binding;
using Xunit;

/// <summary>
/// The view-name search path. Every test here is a case the rule this replaced
/// got wrong or could not express — it was a case-insensitive substring match
/// over the whole relative path, taking the first hit from an unordered list.
/// </summary>
public class ViewNameResolverTests
{
    /// <summary>A small project laid out the conventional way.</summary>
    private static readonly string[] Project =
    [
        "Views/Home/Index.cshtml",
        "Views/Home/_Card.cshtml",
        "Views/Shared/_Card.cshtml",
        "Views/Shared/_Layout.cshtml",
        "Pages/Shared/_Card.cshtml",
        "Views/Admin/Index.cshtml"
    ];

    // ---- Segments, not substrings -----------------------------------------

    [Fact]
    public void Name_MatchesAFileNameNotAnySubstringOfThePath()
    {
        // The motivating defect. In OrchardCore.Users the name "User" matched 100
        // of 167 templates under the old rule; here "Card" must not match
        // "_Card.cshtml", because the file is not called Card.
        var found = ViewNameResolver.Resolve("Card", "Views/Home/Index.cshtml", Project);

        Assert.Empty(found);
    }

    [Fact]
    public void Name_DoesNotMatchADirectoryOfTheSameName()
    {
        // "Home" is a folder, not a view. The old rule matched it against every
        // path under Views/Home and returned one of them.
        var found = ViewNameResolver.Resolve("Home", "Views/Home/Index.cshtml", Project);

        Assert.Empty(found);
    }

    [Fact]
    public void Name_DoesNotMatchALongerFileName()
    {
        var found = ViewNameResolver.Resolve("Ind", "Views/Home/Index.cshtml", Project);

        Assert.Empty(found);
    }

    // ---- Search order ------------------------------------------------------

    [Fact]
    public void Sibling_BeatsShared()
    {
        // A partial next to its caller is the commonest layout, and ASP.NET
        // checks it first. Returning Shared here would bind the wrong file in
        // every project that overrides a shared partial locally.
        var found = ViewNameResolver.Resolve("_Card", "Views/Home/Index.cshtml", Project);

        Assert.Equal("Views/Home/_Card.cshtml", found[0]);
    }

    [Fact]
    public void Shared_IsFoundFromAnUnrelatedFolder()
    {
        var found = ViewNameResolver.Resolve("_Card", "Views/Admin/Index.cshtml", Project);

        Assert.Equal("Views/Shared/_Card.cshtml", found[0]);
    }

    [Fact]
    public void EveryCandidateIsReturned_NotJustTheWinner()
    {
        // A name can legitimately be served by several templates. Collapsing to
        // one hides an override; the caller decides what to do with ambiguity.
        var found = ViewNameResolver.Resolve("_Card", "Views/Home/Index.cshtml", Project);

        Assert.Equal(3, found.Count);
        Assert.Contains("Views/Shared/_Card.cshtml", found);
        Assert.Contains("Pages/Shared/_Card.cshtml", found);
    }

    [Fact]
    public void SharedLookup_PrefersViewsSharedOverPagesShared()
    {
        var found = ViewNameResolver.Resolve("_Card", "Views/Admin/Index.cshtml", Project);

        Assert.True(
            Array.IndexOf(found.ToArray(), "Views/Shared/_Card.cshtml")
            < Array.IndexOf(found.ToArray(), "Pages/Shared/_Card.cshtml"));
    }

    // ---- Explicit paths ----------------------------------------------------

    [Fact]
    public void ExplicitPath_ResolvesExactlyAndDoesNotFallBackToASearch()
    {
        // "~/Views/Shared/_Card.cshtml" names one file. Searching on a miss would
        // invent a match the author did not ask for.
        Assert.Equal(
            "Views/Shared/_Card.cshtml",
            Assert.Single(ViewNameResolver.Resolve("~/Views/Shared/_Card.cshtml", "Views/Home/Index.cshtml", Project)));

        Assert.Empty(ViewNameResolver.Resolve("~/Views/Nope/_Card.cshtml", "Views/Home/Index.cshtml", Project));
    }

    [Fact]
    public void BackslashesAndLeadingSlashesAreNormalised()
    {
        // Extractor paths arrive Windows-shaped; authored names do not.
        var windows = new[] { @"Views\Shared\_Card.cshtml" };

        Assert.Single(ViewNameResolver.Resolve("_Card", @"Views\Admin\Index.cshtml", windows));
    }

    // ---- Absence and stability --------------------------------------------

    [Fact]
    public void UnresolvableName_ReturnsEmptyRatherThanAGuess()
    {
        // Empty is the finding. The old rule's substring fallback meant a missing
        // template almost always bound to something, hiding the real answer.
        Assert.Empty(ViewNameResolver.Resolve("_NoSuchPartial", "Views/Home/Index.cshtml", Project));
    }

    [Fact]
    public void ResultIsIndependentOfCandidateOrder()
    {
        // Saved-graph node order is nondeterministic, so an order-sensitive
        // resolver could bind a different template between runs of identical
        // source. Pinning this makes that class of drift impossible.
        var forward = ViewNameResolver.Resolve("_Card", "Views/Admin/Index.cshtml", Project);
        var reversed = ViewNameResolver.Resolve("_Card", "Views/Admin/Index.cshtml", Project.Reverse());

        Assert.Equal(forward, reversed);
    }

    [Fact]
    public void NameWithExtension_IsTreatedAsAPath()
    {
        Assert.Equal(
            "Views/Shared/_Layout.cshtml",
            Assert.Single(ViewNameResolver.Resolve("_Layout.cshtml", "Views/Home/Index.cshtml", Project)));
    }
}
