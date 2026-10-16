namespace RazorGraph.Tests;

using System.Collections;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The clean environment razorgraph-mcp gives itself: registry machine + user, logon variables,
/// and RAZORGRAPH_*, and nothing else the launching terminal happened to hold.
/// </summary>
/// <remarks>
/// Ported from JanetHome tests\Janet.Tests\ServerEnvironmentTests.cs along with the code; keep the
/// two in step. Driven with dictionaries through Build and Compare, never through Apply: Apply
/// replaces the test runner's own environment, which every other test in the assembly shares.
/// </remarks>
public class ServerEnvironmentTests
{
    /// <summary>A launcher shaped like a Visual Studio x64 Native Tools prompt.</summary>
    private static Hashtable DeveloperPrompt() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["SystemRoot"] = @"C:\Windows",
        ["USERPROFILE"] = @"C:\Users\pat",
        ["LOCALAPPDATA"] = @"C:\Users\pat\AppData\Local",
        ["PATH"] = @"C:\VS\MSBuild\Current\Bin;C:\Windows\system32;C:\Users\pat\.dotnet\tools",
        ["Platform"] = "x64",
        ["VSINSTALLDIR"] = @"C:\VS\",
        ["INCLUDE"] = @"C:\VS\include",
        ["RAZORGRAPH_DEBUG"] = "1",
        ["AZURE_CONFIG_DIR"] = @"C:\elsewhere\.azure",
    };

    private static Hashtable Machine() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Path"] = @"%SystemRoot%\system32;C:\Program Files\PowerShell\7",
        ["PATHEXT"] = ".COM;.EXE;.BAT;.CMD",
        ["TEMP"] = @"%SystemRoot%\TEMP",
        ["OneSetting"] = "machine",
    };

    private static Hashtable User() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Path"] = @"%USERPROFILE%\.dotnet\tools",
        ["TEMP"] = @"%USERPROFILE%\AppData\Local\Temp",
        ["OneSetting"] = "user",
    };

    [Fact]
    public void ADeveloperPromptsVariablesDoNotSurvive()
    {
        var clean = ServerEnvironment.Build(DeveloperPrompt(), Machine(), User());

        // Platform is the one that shapes every graph: MSBuild promotes it to a global property.
        Assert.False(clean.ContainsKey("Platform"));
        Assert.False(clean.ContainsKey("VSINSTALLDIR"));
        Assert.False(clean.ContainsKey("INCLUDE"));

        // Not RAZORGRAPH_ and not in the registry: a session's override, so it does not leak either.
        Assert.False(clean.ContainsKey("AZURE_CONFIG_DIR"));
    }

    [Fact]
    public void LogonVariablesComeFromTheLauncherBecauseTheRegistryDoesNotHoldThem()
    {
        var clean = ServerEnvironment.Build(DeveloperPrompt(), Machine(), User());

        Assert.Equal(@"C:\Windows", clean["SystemRoot"]);
        Assert.Equal(@"C:\Users\pat", clean["USERPROFILE"]);
        Assert.Equal(@"C:\Users\pat\AppData\Local", clean["LOCALAPPDATA"]);
    }

    [Fact]
    public void ALogonValueBeatsTheRegistryBecauseWindowsSetsItAtSignIn()
    {
        // The machine environment really does carry USERNAME=SYSTEM; a sign-in overrides it.
        // janet-mcp's first rotation onto this code applied the registry last and ran as "SYSTEM".
        Hashtable launcher = new(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "pat" };
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "SYSTEM" };

        var clean = ServerEnvironment.Build(launcher, machine, new Hashtable());

        Assert.Equal("pat", clean["USERNAME"]);
        Assert.Empty(ServerEnvironment.Compare(launcher, clean).Changed);
    }

    [Fact]
    public void TheSignInsOwnRecordBeatsALauncherThatIsAlreadyWrong()
    {
        // janet-mcp's second rotation's launcher was the first rotated server, which already
        // believed it was SYSTEM -- so trusting the launcher's "logon" values carried the error forward.
        Hashtable launcher = new(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "SYSTEM", ["USERPROFILE"] = @"C:\Users\pat" };
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "SYSTEM" };
        Hashtable signIn = new(StringComparer.OrdinalIgnoreCase) { ["USERNAME"] = "pat" };

        var clean = ServerEnvironment.Build(launcher, machine, new Hashtable(), signIn);

        Assert.Equal("pat", clean["USERNAME"]);
        Assert.Equal(@"C:\Users\pat", clean["USERPROFILE"]);
        Assert.Equal(["USERNAME"], ServerEnvironment.Compare(launcher, clean).Changed);
    }

    [Fact]
    public void TheServersOwnSettingsSurvive()
    {
        var clean = ServerEnvironment.Build(DeveloperPrompt(), Machine(), User());

        Assert.Equal("1", clean["RAZORGRAPH_DEBUG"]);
    }

    [Fact]
    public void UserWinsOverMachineExceptForPathWhichIsMachineThenUser()
    {
        var clean = ServerEnvironment.Build(DeveloperPrompt(), Machine(), User());

        Assert.Equal("user", clean["OneSetting"]);
        Assert.Equal(@"C:\Users\pat\AppData\Local\Temp", clean["TEMP"]);

        // Expanded, in sign-in order, and without the prompt's MSBuild directory in front.
        Assert.Equal(@"C:\Windows\system32;C:\Program Files\PowerShell\7;C:\Users\pat\.dotnet\tools", clean["Path"]);
    }

    [Fact]
    public void AReferenceToAnUndefinedNameIsLeftAsWritten()
    {
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["Tools"] = @"%NOT_DEFINED%\bin" };

        var clean = ServerEnvironment.Build(new Hashtable(), machine, new Hashtable());

        Assert.Equal(@"%NOT_DEFINED%\bin", clean["Tools"]);
    }

    [Fact]
    public void AValueReferringToAnotherExpandableValueResolves()
    {
        Hashtable launcher = new(StringComparer.OrdinalIgnoreCase) { ["USERPROFILE"] = @"C:\Users\pat" };

        // Outer from the machine and Inner from the user, so Outer is expanded BEFORE Inner: a
        // single pass would copy Inner's still-unexpanded value into it. With both in one table
        // (as JanetHome's version of this test has them) Inner comes first and one pass is
        // enough, so that version passes with the second pass deleted.
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["Outer"] = @"%Inner%\outer" };
        Hashtable user = new(StringComparer.OrdinalIgnoreCase) { ["Inner"] = @"%USERPROFILE%\inner" };

        var clean = ServerEnvironment.Build(launcher, machine, user);

        Assert.Equal(@"C:\Users\pat\inner\outer", clean["Outer"]);
    }

    [Fact]
    public void TheReportNamesWhatChangedAndCarriesNoValues()
    {
        var launcher = DeveloperPrompt();
        var clean = ServerEnvironment.Build(launcher, Machine(), User());

        var report = ServerEnvironment.Compare(launcher, clean);

        Assert.True(report.Applied);
        Assert.Equal(["AZURE_CONFIG_DIR", "INCLUDE", "Platform", "VSINSTALLDIR"], report.Dropped);
        Assert.Equal(["PATH"], report.Changed);
        Assert.Equal(["RAZORGRAPH_DEBUG"], report.KeptFromLauncher);

        // Names only: a launcher's environment can hold secrets, and this report is served.
        var everything = string.Join("|", report.Dropped.Concat(report.Changed).Concat(report.KeptFromLauncher));
        Assert.DoesNotContain("x64", everything);
        Assert.DoesNotContain(@"C:\VS", everything);
    }

    [Fact]
    public void NamesAreComparedCaseInsensitivelyAsWindowsDoes()
    {
        Hashtable launcher = new(StringComparer.OrdinalIgnoreCase) { ["PATH"] = @"C:\a" };
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"C:\a" };

        var clean = ServerEnvironment.Build(launcher, machine, new Hashtable());
        var report = ServerEnvironment.Compare(launcher, clean);

        // PATH and Path are one variable: neither dropped nor changed.
        Assert.Empty(report.Dropped);
        Assert.Empty(report.Changed);
    }
}
