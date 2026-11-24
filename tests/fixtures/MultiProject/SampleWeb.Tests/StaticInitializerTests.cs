using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. Touches two types whose static initialization
/// calls methods nothing else calls: one by reading a static property, one by
/// building it. Touching a type runs its static initializer, so the coverage walk
/// steps from each test onto the type and from the type along the edges its
/// static initialization starts.
/// </summary>
public class StaticInitializerTests
{
    [Fact]
    public void Registry_HasAFallback()
    {
        Assert.NotNull(PartRegistry.Fallback);
    }

    [Fact]
    public void DrawnWorld_CanBeBuilt()
    {
        Assert.NotNull(new DrawnWorld());
    }

    /// <summary>
    /// Touches IWorldStamp through its static member and nothing else. Its
    /// implementer StampedWorld is never built or named here, so that type's
    /// static initialization does not run.
    /// </summary>
    [Fact]
    public void WorldStamp_HasAVersion()
    {
        Assert.Equal(1, IWorldStamp.Version());
    }
}
