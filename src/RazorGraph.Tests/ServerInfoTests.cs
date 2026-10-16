namespace RazorGraph.Tests;

using System.Collections;
using System.Text.Json;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// server_info reports what normalising the environment did -- by variable name, never by value,
/// because a launcher's environment can hold secrets and this answer lands in transcripts.
/// </summary>
/// <remarks>
/// The report is passed in rather than produced by ServerEnvironment.Apply, which rewrites the
/// environment every test in this process shares.
/// </remarks>
public class ServerInfoTests
{
    [Fact]
    public void Environment_NamesWhatWasDroppedChangedAndKept_AndCarriesNoValues()
    {
        Hashtable launcher = new(StringComparer.OrdinalIgnoreCase)
        {
            ["PATH"] = @"C:\VS\MSBuild\Current\Bin;C:\Windows\system32",
            ["Platform"] = "x64",
            ["SECRET_TOKEN"] = "hunter2-do-not-serve",
            ["RAZORGRAPH_DEBUG"] = "kept-value-not-served",
        };
        Hashtable machine = new(StringComparer.OrdinalIgnoreCase) { ["Path"] = @"C:\Windows\system32" };
        var report = ServerEnvironment.Compare(launcher, ServerEnvironment.Build(launcher, machine, new Hashtable()));

        var json = new ServerTools(new GraphStore()).ServerInfo(report);

        using var doc = JsonDocument.Parse(json);
        var environment = doc.RootElement.GetProperty("environment");
        Assert.True(environment.GetProperty("applied").GetBoolean());
        Assert.False(environment.TryGetProperty("skippedBecause", out _));
        Assert.Equal(["Platform", "SECRET_TOKEN"], Names(environment, "dropped"));
        Assert.Equal(["PATH"], Names(environment, "changed"));
        Assert.Equal(["RAZORGRAPH_DEBUG"], Names(environment, "keptFromLauncher"));

        // Anywhere in the answer, not just the environment section.
        Assert.DoesNotContain("hunter2", json);
        Assert.DoesNotContain("kept-value", json);
        Assert.DoesNotContain("x64", json);
        Assert.DoesNotContain("MSBuild", json);
    }

    [Fact]
    public void Environment_IsPresentAndSaysSoWhenNormalisationNeverRanInThisProcess()
    {
        // The test host never calls Apply, so this is the public tool exactly as a host that did
        // not start through Program would serve it: present, and saying why nothing was applied.
        using var doc = JsonDocument.Parse(new ServerTools(new GraphStore()).ServerInfo());

        var environment = doc.RootElement.GetProperty("environment");
        Assert.False(environment.GetProperty("applied").GetBoolean());
        Assert.StartsWith("not run", environment.GetProperty("skippedBecause").GetString());
        Assert.Empty(Names(environment, "dropped"));
        Assert.Empty(Names(environment, "changed"));
        Assert.Empty(Names(environment, "keptFromLauncher"));
    }

    private static string[] Names(JsonElement environment, string list) =>
        [.. environment.GetProperty(list).EnumerateArray().Select(e => e.GetString()!)];
}
