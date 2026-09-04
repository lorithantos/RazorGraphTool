namespace RazorGraph.Tests;

using RazorGraph.Extractor.Roslyn;
using Xunit;

/// <summary>
/// The version half of the analyzer-host guard. Pure comparison, so it is
/// tested against the versions that actually caused the failure rather than
/// against whatever this machine's SDK happens to ship.
/// </summary>
public class AnalyzerHostCheckTests
{
    // The real numbers: Microsoft.CodeAnalysis 5.6.0 in RazorGraph.Extractor
    // against the Roslyn in .NET SDK 10.0.400. The Razor generator contributed
    // zero generators, and no Razor page class reached the graph.
    private static readonly Version Host56 = new(5, 600, 26, 26310);
    private static readonly Version Sdk59 = new(5, 900, 26, 38015);

    [Fact]
    public void Warns_WhenHostRoslynIsOlderThanTheSdks()
    {
        var warning = AnalyzerHostCheck.VersionWarning(Host56, Sdk59);

        Assert.NotNull(warning);
        // The warning has to carry the fix, not just the diagnosis: whoever
        // reads it is not the person who debugged this.
        Assert.Contains("RazorGraph.Extractor.csproj", warning!);
        Assert.Contains("5.9", warning);
    }

    [Fact]
    public void Silent_WhenVersionsMatch()
    {
        Assert.Null(AnalyzerHostCheck.VersionWarning(Sdk59, Sdk59));
    }

    [Fact]
    public void Silent_WhenOnlyTheServicingBuildDiffers()
    {
        // The measured configuration this repo actually ships: package 5.9.0 is
        // an EARLIER build of the same release line than the SDK's, and it loads
        // every one of that SDK's generators. Comparing all four fields called
        // this a mismatch — the check's first act was to cry wolf about a
        // working setup, which is how a warning teaches people to ignore it.
        var package590 = new Version(5, 900, 26, 35703);

        Assert.Null(AnalyzerHostCheck.VersionWarning(package590, Sdk59));
    }

    [Fact]
    public void Silent_WhenHostRoslynIsNewerThanTheSdks()
    {
        // Supported direction: the SDK's generators load into a newer host.
        // Warning here would fire on every machine whose SDK trails the package
        // feed, and a warning that is usually noise stops being read.
        Assert.Null(AnalyzerHostCheck.VersionWarning(Sdk59, Host56));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void Silent_WhenEitherVersionIsUnknown(bool hasHost, bool hasSdk)
    {
        // Unknown is "cannot tell", never "compatible" — but an unprovable
        // warning would be worse than none, so the check declines to guess.
        Assert.Null(AnalyzerHostCheck.VersionWarning(
            hasHost ? Host56 : null,
            hasSdk ? Sdk59 : null));
    }

    [Fact]
    public void SdkRoslynVersion_IsNull_ForAPathWithNoRoslyn()
    {
        Assert.Null(AnalyzerHostCheck.SdkRoslynVersion(Path.GetTempPath()));
        Assert.Null(AnalyzerHostCheck.SdkRoslynVersion(null));
    }

    [Fact]
    public void HostRoslynVersion_IsDiscoverable()
    {
        // If this ever returns null the predictive half of the guard is dead,
        // and it would die silently — exactly the failure mode it exists for.
        Assert.NotNull(AnalyzerHostCheck.HostRoslynVersion());
    }

    [Fact]
    public void ThisBuild_RunsARoslynAtLeastAsNewAsTheSdks()
    {
        // The live assertion, and the regression test for the pin itself: if a
        // future SDK outruns the pinned Microsoft.CodeAnalysis.* packages, this
        // fails here rather than showing up as a quietly incomplete graph.
        RoslynExtractor.EnsureMsBuildRegistered();

        var warning = AnalyzerHostCheck.VersionWarning(
            AnalyzerHostCheck.HostRoslynVersion(),
            AnalyzerHostCheck.SdkRoslynVersion(RoslynExtractor.SdkPath));

        Assert.True(warning == null, warning);
    }
}
