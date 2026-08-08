using Microsoft.AspNetCore.Mvc.RazorPages;
using SampleLib;

namespace SampleWeb.Pages;

public class IndexModel : PageModel
{
    private readonly ICatalogStore _store;

    public IndexModel(ICatalogStore store) => _store = store;

    public IReadOnlyList<string> Catalogs { get; private set; } = Array.Empty<string>();

    public void OnGet()
    {
        // The cross-project call the single-project graph could never see.
        Catalogs = _store.List();

        // The cross-project member READ with the same property: a Reads edge
        // in the solution graph, invisible in a single-project build.
        _ = PriceBook.Lookups;
    }
}
