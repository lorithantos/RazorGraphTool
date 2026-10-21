namespace RazorGraph.Tests;

using System.Diagnostics;
using Janet.Mcp.Security;
using RazorGraph.Mcp;
using Xunit;

/// <summary>Splitting --auth-token-command into an executable and its arguments.</summary>
public class CommandLineTests
{
    [Fact]
    public void WhitespaceSeparatesTokens() =>
        Assert.Equal(["janet", "mcp-token", "--print"], CommandLine.Split("  janet   mcp-token\t--print "));

    [Fact]
    public void DoubleQuotesKeepWhitespaceAndAreRemoved() =>
        Assert.Equal(
            [@"C:\Program Files\Tool\tool.exe", "--name", "a b"],
            CommandLine.Split(@"""C:\Program Files\Tool\tool.exe"" --name ""a b"""));

    [Fact]
    public void SingleQuotesInsideDoubleQuotesReachTheCommand() =>
        Assert.Equal(["pwsh", "-NoProfile", "-Command", "'abc'"], CommandLine.Split("""pwsh -NoProfile -Command "'abc'" """));

    [Fact]
    public void AQuotedSpanJoinsTheTextAroundIt() =>
        Assert.Equal(["--x=a b"], CommandLine.Split("--x=\"a b\""));

    [Fact]
    public void ABackslashQuoteIsALiteralQuote() =>
        Assert.Equal(["say", "he said \"hi\""], CommandLine.Split("say \"he said \\\"hi\\\"\""));

    [Fact]
    public void AnEmptyQuotedArgumentIsKept() =>
        Assert.Equal(["tool", "", "x"], CommandLine.Split("tool \"\" x"));

    [Fact]
    public void AnUnterminatedQuoteIsRefused() =>
        Assert.Throws<FormatException>(() => CommandLine.Split("tool \"unfinished"));

    [Fact]
    public void AnEmptyCommandLineNamesNoCommand() =>
        Assert.Throws<FormatException>(() => new CommandTokenProvider("   "));
}

/// <summary>
/// The provider behind --auth-token-command, run against real tiny commands: cmd.exe is always
/// there on Windows, so no new dependency is needed.
/// </summary>
public class CommandTokenProviderTests
{
    [Fact]
    public void StdoutTrimmedIsTheToken()
    {
        CommandTokenProvider provider = new("cmd /c echo   abc-token  ");

        Assert.Equal("abc-token", provider.ReadToken());
    }

    [Fact]
    public void QuotedArgumentsReachTheCommandIntact()
    {
        // Parsed to [cmd, /c, echo, "two words"]; cmd echoes its argument with the quotes the
        // runner added, so seeing both words together proves the space survived as one argument.
        CommandTokenProvider provider = new("cmd /c echo \"two words\"");

        Assert.Equal(["/c", "echo", "two words"], provider.Arguments);
        Assert.Contains("two words", provider.ReadToken());
    }

    [Fact]
    public void ANonZeroExitThrowsWithItsStderr()
    {
        CommandTokenProvider provider = new("cmd /c echo secret-on-stdout & echo vault is locked 1>&2 & exit 3");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(provider.ReadToken);

        Assert.Contains("code 3", ex.Message);
        Assert.Contains("vault is locked", ex.Message);
        Assert.DoesNotContain("secret-on-stdout", ex.Message);
    }

    [Fact]
    public void ACommandPastItsTimeoutIsKilledAndThrows()
    {
        // ping -n 30 takes about 29 s; the limit is 1 s.
        CommandTokenProvider provider = new("cmd /c ping -n 30 127.0.0.1", TimeSpan.FromSeconds(1));
        Stopwatch clock = Stopwatch.StartNew();

        TimeoutException ex = Assert.Throws<TimeoutException>(provider.ReadToken);

        clock.Stop();
        Assert.Contains("killed", ex.Message);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
    }

    [Fact]
    public void TheDefaultTimeoutIsTenSeconds() =>
        Assert.Equal(TimeSpan.FromSeconds(10), CommandTokenProvider.DefaultTimeout);

    [Fact]
    public void AnUnknownExecutableSaysItIsNotOnPath()
    {
        CommandTokenProvider provider = new("no-such-token-tool-7c3e --print");

        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(provider.ReadToken);

        Assert.Contains("'no-such-token-tool-7c3e' was not found on PATH", ex.Message);
    }

    [Fact]
    public void TheAuthenticatorRecordsAFailingCommandAsItsProviderError()
    {
        CommandTokenProvider provider = new("cmd /c echo vault is locked 1>&2 & exit 3");
        using BearerAuthenticator bearer = new(BearerMode.Report, provider.ReadToken);

        BearerAuthSnapshot snapshot = bearer.Snapshot();

        Assert.False(snapshot.TokenConfigured);
        Assert.Contains("vault is locked", snapshot.ProviderError);
    }

    [Fact]
    public void PwshWorksAsATokenCommand()
    {
        CommandTokenProvider provider = new("""pwsh -NoProfile -NonInteractive -Command "'abc'" """);

        Assert.Equal("abc", provider.ReadToken());
    }
}

/// <summary>Resolving the executable through PATH and PATHEXT, without running anything.</summary>
public class ExecutableResolverTests
{
    [Fact]
    public void ABareNameIsFoundOnPathWithAPathExtExtension()
    {
        string directory = Directory.CreateTempSubdirectory("rg-resolve-").FullName;
        try
        {
            string tool = Path.Combine(directory, "token-tool.exe");
            File.WriteAllText(tool, string.Empty);

            string resolved = ExecutableResolver.Resolve(
                "token-tool", @"C:\nowhere-7c3e" + Path.PathSeparator + directory, ".COM;.EXE");

            Assert.Equal(tool, resolved, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void AMissingFullPathIsRefusedByName()
    {
        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => ExecutableResolver.Resolve(@"C:\nowhere-7c3e\tool.exe", null, null));

        Assert.Contains(@"C:\nowhere-7c3e\tool.exe", ex.Message);
    }
}

/// <summary>The --auth options, and the start-up refusal they can produce.</summary>
public class HttpAuthOptionTests
{
    [Fact]
    public void RequireIsTheDefault() =>
        Assert.Equal(BearerMode.Require, HttpAuth.DefaultMode);

    [Theory]
    [InlineData("require", BearerMode.Require)]
    [InlineData("Report", BearerMode.Report)]
    [InlineData("OFF", BearerMode.Off)]
    public void ModesParse(string value, BearerMode expected)
    {
        Assert.True(HttpAuth.TryParseMode(value, out BearerMode mode));
        Assert.Equal(expected, mode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("on")]
    [InlineData("2")]
    [InlineData(null)]
    public void OtherModesAreRefused(string? value) =>
        Assert.False(HttpAuth.TryParseMode(value, out _));

    [Fact]
    public void RequireWithoutACommandRefusesNamingTheOption()
    {
        string? error = HttpAuth.StartupError(BearerMode.Require, null);

        Assert.NotNull(error);
        Assert.Contains("--auth-token-command", error);
    }

    [Theory]
    [InlineData(BearerMode.Report, null)]
    [InlineData(BearerMode.Off, null)]
    [InlineData(BearerMode.Require, "janet mcp-token --print")]
    public void EverythingElseMayStart(BearerMode mode, string? command) =>
        Assert.Null(HttpAuth.StartupError(mode, command));

    /// <remarks>
    /// The real server executable, which the project reference copies next to the tests, on a
    /// free port nothing else serves. Were the refusal missing it would start and listen, and the
    /// runner's timeout would kill it and fail the test.
    /// </remarks>
    [Fact]
    public void TheServerRefusesToStartRequireWithoutACommand()
    {
        string server = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RazorGraph.Mcp.exe" : "RazorGraph.Mcp");
        System.Net.Sockets.TcpListener probe = new(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        int port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();

        CommandResult result = CommandRunner.Run(server, ["--http", "--port", port.ToString()], TimeSpan.FromSeconds(30));

        Assert.Equal(2, result.ExitCode);
        Assert.Contains("will not start", result.StandardError);
        Assert.Contains("--auth-token-command", result.StandardError);
    }
}
