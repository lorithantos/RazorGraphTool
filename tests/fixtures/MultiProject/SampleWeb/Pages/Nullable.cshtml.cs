using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SampleWeb.Pages;

/// <summary>The page model Nullable.cshtml names as <c>NullableModel?</c>.</summary>
public class NullableModel : PageModel
{
    public string Name { get; set; } = "";

    public NullableCard? Card { get; set; }
}

/// <summary>The plain class _NullableCard.cshtml names as <c>NullableCard?</c>.</summary>
public class NullableCard
{
    // Not "Title": Index.cshtml's ViewData["Title"] literal would quote it.
    public string Caption { get; set; } = "";
}
