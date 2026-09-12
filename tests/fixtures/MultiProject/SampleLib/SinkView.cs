namespace SampleLib;

/// <summary>
/// The code-behind half of Views/SinkView.xaml, named by that file's x:Class.
///
/// <para>
/// A plain class rather than a real Window, deliberately. The extractor reads
/// XAML as text and never compiles it, so the fixture needs no WPF SDK — and
/// requiring one would pin the whole fixture solution to Windows desktop for
/// every integration run, which is a large cost for a small file.
/// </para>
/// </summary>
public sealed class SinkView
{
    /// <summary>
    /// Bound from the view, and reachable ONLY through the code-behind route:
    /// nothing classified as a view model declares a Caption, so a candidate walk
    /// that looked at view models alone would report this binding unresolved.
    /// </summary>
    /// <remarks>
    /// NOT called Title, which it was for about a minute. The Razor fixture page
    /// writes ViewData["Title"], a literal that matched no unique declaration
    /// until this property existed — at which point Quotes minted an edge from a
    /// Razor page to a WPF view model that have nothing to do with each other,
    /// and the pinned Quotes count caught it. A fixture member named after a
    /// common framework key manufactures exactly the coincidence the Quotes
    /// caveat warns about.
    /// </remarks>
    public string Caption => "the sink view";
}
