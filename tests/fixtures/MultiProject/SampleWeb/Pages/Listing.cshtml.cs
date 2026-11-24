using Microsoft.AspNetCore.Mvc.RazorPages;

namespace SampleWeb.Pages;

/// <summary>A generic page model: Listing.cshtml names it closed, as <c>ListingModel&lt;NullableCard&gt;</c>.</summary>
public class ListingModel<T> : PageModel
{
    public string Heading { get; set; } = "";
}

/// <summary>A generic model the solution declares: _PagedCards.cshtml names it closed over NullableCard.</summary>
public class Paged<T>
{
    public int PageIndex { get; set; }
}
