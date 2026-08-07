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

/// <summary>
/// The invisible-registration case: no source call site anywhere reaches
/// InvokeAsync — the framework discovers the type through DI and calls in
/// through the interface it declared.
/// </summary>
public class FaultyMiddleware : Microsoft.AspNetCore.Http.IMiddleware
{
    public Task InvokeAsync(
        Microsoft.AspNetCore.Http.HttpContext context,
        Microsoft.AspNetCore.Http.RequestDelegate next)
    {
        Throwing.UnguardedThrow();
        return next(context);
    }
}

/// <summary>
/// Delegate registrations. CompareItems is handed to a BCL method as a method
/// group — a callback the framework can invoke with no call site in source.
/// Format goes only to an in-solution field: a delegate edge, not an entry
/// point. RegisterLambda hands a lambda out-of-solution, so the container
/// stands in as the callback surface. HoldThrowingLambda's lambda stays
/// in-solution: its throw is attributed here as conditional, but nothing
/// makes it an entry point.
/// </summary>
public class CallbackHost
{
    private Func<double, string>? _format;

    public void RegisterComparer()
    {
        var items = new List<int> { 2, 1 };
        items.Sort(CompareItems);
    }

    private static int CompareItems(int left, int right)
        => Throwing.UnguardedThrow();

    public void KeepFormatter()
    {
        _format = Format;
    }

    public string? Describe(double value) => _format?.Invoke(value);

    private static string Format(double value) => value.ToString("F2");

    public void RegisterLambda()
    {
        var items = new List<int> { 1 };
        items.RemoveAll(x => Throwing.UnguardedThrow() > x);
    }

    public Action HoldThrowingLambda()
    {
        Action held = () => throw new CustomException("held lambda");
        return held;
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
