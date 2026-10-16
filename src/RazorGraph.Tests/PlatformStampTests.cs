namespace RazorGraph.Tests;

using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// The rule that decides what a graph says about its MSBuild Platform, taken
/// with its inputs as arguments. The builder reads the real environment, which
/// a test cannot set without racing every other test in the process; these pin
/// the rule itself, and the integration tests pin that a build applies it.
/// </summary>
public class PlatformStampTests
{
    [Fact]
    public void Requested_WinsOverTheEnvironment()
    {
        Assert.Equal(("ARM64", "requested"), GraphBuilder.ResolvePlatform("ARM64", "x64"));
    }

    [Fact]
    public void Environment_IsRecordedWhenNothingWasRequested()
    {
        Assert.Equal(("x64", "environment"), GraphBuilder.ResolvePlatform(null, "x64"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Neither_IsProjectDefault_WithNoPlatform(string? environment)
    {
        Assert.Equal(((string?)null, "projectDefault"), GraphBuilder.ResolvePlatform(null, environment));
    }

    /// <summary>
    /// A blank request is no request. Passed through, it would become an empty
    /// global property, which overrides the environment rather than deferring to
    /// it -- and the stamp would call that "requested".
    /// </summary>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task BlankPlatform_IsNotARequest(string blank)
    {
        await using var builder = new GraphBuilder { Platform = blank };

        Assert.Null(builder.Platform);
    }

    [Fact]
    public async Task Platform_IsTrimmed()
    {
        await using var builder = new GraphBuilder { Platform = " x64 " };

        Assert.Equal("x64", builder.Platform);
    }
}
