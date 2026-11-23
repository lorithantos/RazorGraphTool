using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. Reads a property whose getter calls a method,
/// so the coverage walk has a property-sourced Calls edge within reach: reading
/// Cached steps onto the property, and the getter's call covers PartShelf.Restock.
/// Nothing reads Fresh or the indexer, so what those call stays uncovered.
/// </summary>
public class PartShelfTests
{
    [Fact]
    public void Cached_IsBuiltOnce()
    {
        var shelf = new PartShelf();

        Assert.Same(shelf.Cached, shelf.Cached);
    }
}
