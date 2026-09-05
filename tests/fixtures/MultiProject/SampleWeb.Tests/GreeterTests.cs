using SampleLib;
using Xunit;

namespace SampleWeb.Tests;

/// <summary>
/// Fixture only — never executed. The call binds to the interface member, so
/// the only way coverage reaches Greeter.Greet is through the interface map.
/// </summary>
public class GreeterTests
{
    [Fact]
    public void Greet_ThroughTheInterface()
    {
        IGreeter greeter = new Greeter();

        Assert.Equal("hello x", greeter.Greet("x"));
    }
}
