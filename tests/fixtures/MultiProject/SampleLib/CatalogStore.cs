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

    /// <summary>
    /// Called only from a test class's constructor — the ctor-setup coverage
    /// case. Same isolation rationale as Preload.
    /// </summary>
    public int Warm() => 1;

    /// <summary>
    /// Called only from CatalogCache's field initializer — the
    /// initializer-runs-in-the-implicit-ctor coverage case.
    /// </summary>
    public static int Seed() => 3;
}

/// <summary>
/// No explicit constructor: the implicit ctor exists as a graph node only
/// because this initializer is the code it runs. A test newing this up must
/// cover the ctor at depth 1 and Seed at depth 2.
/// </summary>
public class CatalogCache
{
    private readonly int _seed = CatalogStore.Seed();

    public int Size() => _seed;
}
