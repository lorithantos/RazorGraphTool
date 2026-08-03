using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. It exists so the extractor has a [Fact] whose
/// call chain crosses into SampleLib.
/// </summary>
public class CatalogStoreTests
{
    [Fact]
    public void List_ReturnsSortedCatalogs()
    {
        // using, not var: Dispose is called with no invocation syntax, the
        // implicit-disposal coverage case.
        using var store = new CatalogStore();

        var result = store.List();

        Assert.Equal(new[] { "alpha", "beta" }, result);
    }

    /// <summary>Not a test method: no attribute, so it must not emit Covers edges.</summary>
    public void NotATest()
    {
        new CatalogStore().Orphan();
    }
}
