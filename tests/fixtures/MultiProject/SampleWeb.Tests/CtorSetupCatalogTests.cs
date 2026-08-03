using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. Setup happens in the constructor, xUnit's
/// primary setup idiom: the framework runs it before every test, no test calls
/// it. Warm must end up covered by Warm_IsPositive even though the only call to
/// it sits in the ctor.
/// </summary>
public class CtorSetupCatalogTests
{
    private readonly int _warmth;

    public CtorSetupCatalogTests()
    {
        _warmth = new CatalogStore().Warm();
    }

    [Fact]
    public void Warm_IsPositive()
    {
        Assert.Equal(1, _warmth);
    }
}
