using Microsoft.AspNetCore.Mvc.RazorPages;
using SampleLib;

namespace SampleWeb;

/// <summary>
/// Entry-point fixtures for exception-escape analysis: each member is a place
/// a framework calls into user code, wired to the SampleLib Throwing scenarios.
/// Nothing else in the fixtures calls these — they exist to be reached by the
/// runtime, which is the point. Do not add a .cshtml for FaultyModel; the
/// PageModel classification is base-type driven and a page file would disturb
/// the single-RazorPage assertions elsewhere.
/// </summary>
public class FaultyModel : PageModel
{
    /// <summary>The cross-project escape: SampleLib's throw surfaces here.</summary>
    public void OnGet()
    {
        Throwing.UnguardedThrow();
    }

    /// <summary>The guarded twin — nothing escapes a catch of the base of bases.</summary>
    public void OnPost()
    {
        try
        {
            Throwing.UnguardedThrow();
        }
        catch (Exception)
        {
            // swallow: the fixture's point is the absence of an Escapes edge
        }
    }
}

public class Widget
{
    /// <summary>Event-handler shape: (object?, EventArgs) and void.</summary>
    public void OnTick(object? sender, EventArgs e)
    {
        Throwing.UnguardedThrow();
    }

    /// <summary>A chain whose only handling is a when filter — escapes as conditional.</summary>
    public void OnFilteredTick(object? sender, EventArgs e)
    {
        Throwing.FilteredCaller();
    }

    /// <summary>
    /// async void: the exception rethrows on the sync context, no caller can
    /// catch it. Throws directly — the depth-0 self-escape case.
    /// </summary>
    public async void FireAndForget()
    {
        await Task.Yield();
        throw new InvalidOperationException("fire-and-forget");
    }
}
