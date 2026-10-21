using Janet.Mcp.Security;

namespace RazorGraph.Mcp;

/// <summary>
/// Reads the bearer token by running a configured command, and trims its stdout to the token.
/// </summary>
/// <remarks>
/// The server knows nothing about where a token is kept: whoever starts it names a command, and
/// whatever that command prints is the token. It is called by <see cref="BearerAuthenticator"/>'s
/// single refresh consumer, at most once per <see cref="BearerAuthenticator.RefreshInterval"/>, so
/// there is no caching here. A failure throws; the authenticator keeps the message as its
/// ProviderError and /health shows it. Messages name the executable and carry its stderr, never its
/// stdout, which is the token, and never its arguments.
/// </remarks>
internal sealed class CommandTokenProvider
{
    /// <summary>How long the command may run before it is killed and the read fails.</summary>
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(10);

    private readonly TimeSpan timeout;

    /// <summary>Parses the command line once, so a malformed one is refused at startup.</summary>
    /// <param name="commandLine">An executable, resolved through PATH, then its arguments.</param>
    /// <param name="timeout">The limit on one run; <see cref="DefaultTimeout"/> when null.</param>
    /// <exception cref="FormatException">The command line is empty or has an unterminated quote.</exception>
    internal CommandTokenProvider(string commandLine, TimeSpan? timeout = null)
    {
        IReadOnlyList<string> tokens = CommandLine.Split(commandLine);
        if (tokens.Count == 0 || string.IsNullOrWhiteSpace(tokens[0]))
        {
            throw new FormatException($"{HttpAuth.TokenCommandOption} names no command.");
        }

        Executable = tokens[0];
        Arguments = tokens.Skip(1).ToArray();
        this.timeout = timeout ?? DefaultTimeout;
    }

    /// <summary>The executable as written, before PATH resolution.</summary>
    internal string Executable { get; }

    /// <summary>The arguments, passed to the executable one by one.</summary>
    internal IReadOnlyList<string> Arguments { get; }

    /// <summary>Runs the command and returns its trimmed stdout, or null when it printed nothing.</summary>
    /// <exception cref="FileNotFoundException">The executable is not on PATH.</exception>
    /// <exception cref="TimeoutException">The command ran past the timeout and was killed.</exception>
    /// <exception cref="InvalidOperationException">The command exited non-zero.</exception>
    internal string? ReadToken()
    {
        string path = ExecutableResolver.Resolve(
            Executable,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATHEXT"));

        CommandResult result = CommandRunner.Run(path, Arguments, timeout);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{Executable}' exited with code {result.ExitCode}." + CommandRunner.Describe(result.StandardError));
        }

        string token = result.StandardOutput.Trim();
        return token.Length == 0 ? null : token;
    }
}

/// <summary>The HTTP transport's bearer options: what they accept, and what they refuse to start with.</summary>
internal static class HttpAuth
{
    internal const string ModeOption = "--auth";
    internal const string TokenCommandOption = "--auth-token-command";

    /// <summary>What <c>--auth</c> is when it is not given.</summary>
    internal const BearerMode DefaultMode = BearerMode.Require;

    /// <summary>Reads an <c>--auth</c> value: require, report or off, in any case.</summary>
    internal static bool TryParseMode(string? value, out BearerMode mode)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "require":
                mode = BearerMode.Require;
                return true;
            case "report":
                mode = BearerMode.Report;
                return true;
            case "off":
                mode = BearerMode.Off;
                return true;
            default:
                mode = DefaultMode;
                return false;
        }
    }

    /// <summary>
    /// Why the HTTP server must not start with these options, or null when it may. Require with no
    /// token command could only ever answer 401, so it is refused rather than started.
    /// </summary>
    internal static string? StartupError(BearerMode mode, string? tokenCommand) =>
        mode == BearerMode.Require && string.IsNullOrWhiteSpace(tokenCommand)
            ? $"{ModeOption} require (the default) needs {TokenCommandOption} \"<command line>\": the command whose " +
              "output is the bearer token clients must send. Pass it, or pass " +
              $"{ModeOption} report to count requests without refusing any, or {ModeOption} off."
            : null;

    /// <summary>
    /// The token provider for these options: the command's reader, or one that has no token when
    /// there is no command (report and off may start without one).
    /// </summary>
    /// <exception cref="FormatException">The command line is malformed.</exception>
    internal static Func<string?> CreateProvider(string? tokenCommand) =>
        string.IsNullOrWhiteSpace(tokenCommand)
            ? static () => null
            : new CommandTokenProvider(tokenCommand).ReadToken;
}
