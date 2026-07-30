namespace SampleLib;

public interface ICatalogStore
{
    IReadOnlyList<string> List();
}

/// <summary>
/// Two levels deep on purpose: List is called directly by both the web project
/// and the test project, while Normalize is only reachable through it. That gives
/// the coverage extractor a depth-1 and a depth-2 case to distinguish.
/// </summary>
public class CatalogStore : ICatalogStore
{
    public IReadOnlyList<string> List() => Normalize(new[] { "beta", "alpha" });

    private static IReadOnlyList<string> Normalize(IEnumerable<string> items) =>
        items.OrderBy(i => i, StringComparer.Ordinal).ToList();

    /// <summary>Reachable from nothing — the uncovered-method case.</summary>
    public string Orphan() => "unreferenced";
}
