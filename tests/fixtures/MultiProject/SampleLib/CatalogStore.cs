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
public class CatalogStore : ICatalogStore, IDisposable
{
    /// <summary>
    /// A production Dispose on a type with no tests: must NOT be flagged
    /// isTestLifecycle — the negative case for the lifecycle-hook gate.
    /// </summary>
    public void Dispose() { }

    public IReadOnlyList<string> List() => Normalize(new[] { "beta", "alpha" });

    private static IReadOnlyList<string> Normalize(IEnumerable<string> items) =>
        items.OrderBy(i => i, StringComparer.Ordinal).ToList();

    /// <summary>Reachable from nothing — the uncovered-method case.</summary>
    public string Orphan() => "unreferenced";

    /// <summary>
    /// Called only from a test class's InitializeAsync — the lifecycle-setup
    /// coverage case. Deliberately calls nothing else, so it does not add a
    /// second covering test to List/Normalize and disturb their depth asserts.
    /// </summary>
    public int Preload() => 2;
}
