namespace RazorGraph.Tests;

using System.CommandLine;
using RazorGraph.Cli;
using Xunit;

/// <summary>
/// Help and usage name the command people type, razorgraph, rather than the assembly the tool
/// host runs. A RootCommand would take its name from the running process, which under the test
/// host is testhost and under a .NET tool shim is RazorGraph.Cli, so a name here that says
/// razorgraph can only have come from the command tree itself.
/// </summary>
public class CliHelpNameTests
{
    [Fact]
    public void RootHelp_UsageNamesRazorgraph()
    {
        var output = Invoke("--help");

        Assert.Contains("razorgraph [command] [options]", output);
        Assert.DoesNotContain("RazorGraph.Cli", output);
        Assert.DoesNotContain("testhost", output);
    }

    [Fact]
    public void SubcommandHelp_UsageStartsFromRazorgraph()
    {
        var output = Invoke("query", "--help");

        Assert.Contains("razorgraph query", output);
        Assert.DoesNotContain("testhost", output);
    }

    [Fact]
    public void RootKeepsTheVersionOptionARootCommandWouldHaveAdded()
    {
        Assert.Contains(CliRoot.Create().Options, o => o is VersionOption);
    }

    private static string Invoke(params string[] args)
    {
        using var output = new StringWriter();
        var exit = CliRoot.Create().Parse(args).Invoke(new InvocationConfiguration { Output = output, Error = output });
        Assert.Equal(0, exit);
        return output.ToString();
    }
}
