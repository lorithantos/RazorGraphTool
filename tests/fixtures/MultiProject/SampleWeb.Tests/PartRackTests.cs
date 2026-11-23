using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. Reads through PartRack's int indexer and
/// nothing else, so the coverage walk steps onto that indexer through an
/// element access and its getter's call covers PartRack.BySlot. The string
/// indexer is not read here, so ByName stays uncovered by this test.
/// </summary>
public class PartRackTests
{
    [Fact]
    public void Slot_ReadsThroughTheIntIndexer()
    {
        var rack = new PartRack();

        Assert.Equal(8, rack[0].Value);
    }
}
