using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. Reads a property whose getter calls a method,
/// so the coverage walk has a property-sourced Calls edge within reach. It does
/// not step through a property node, so PartShelf.Restock stays uncovered: the
/// edge is in the graph for navigation, and coverage does not follow it yet.
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
