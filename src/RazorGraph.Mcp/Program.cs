using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RazorGraph.Mcp;

// The `razorgraph-mcp` server. Two transports, for two different moments:
//
//   stdio  -- the default, and what consumers get. Zero configuration, no port, supported by
//             every MCP client. The client owns the process.
//   http   -- opt-in, and what you develop against. The server is a SEPARATE process the client
//             dials, so it can be killed and rebuilt while a session stays open: Claude Code
//             reconnects an HTTP server automatically with exponential backoff (five attempts,
//             1s doubling). A stdio server is a child process the client holds open and never
//             reconnects, which is why every change here used to cost a session restart.
//
// Ported from Janet.Mcp, which had already paid for the mistakes -- see the /health probe and
// the AddressInUse race below, both of which exist because the obvious version of this crashed.

// Fixed rather than derived. Janet derives its port from the graph it serves, because one
// janet-mcp serves exactly one catalog; a razorgraph-mcp holds a registry of many graphs and has
// no such identity to hash. One well-known port, overridable, is the honest version of that.
const int DefaultPort = 7718;

// Which build this process is, read through the 'current' junction before anything else can
// happen: a rotation repoints the junction while the old process is still answering, and
// server_info must name the build this process loaded, not the one the junction names later.
BuildLocation.Capture();

bool http = false;
bool printUrl = false;
int port = 0;
BearerMode authMode = HttpAuth.DefaultMode;
string? authTokenCommand = null;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--http": http = true; break;
        case "--print-url": printUrl = true; break;
        case HttpAuth.ModeOption when i + 1 < args.Length:
            if (!HttpAuth.TryParseMode(args[++i], out authMode))
            {
                Console.Error.WriteLine($"{HttpAuth.ModeOption} takes require, report or off, not '{args[i]}'.");
                return 2;
            }

            break;
        case HttpAuth.TokenCommandOption when i + 1 < args.Length: authTokenCommand = args[++i]; break;
        case "--help":
            Console.Error.WriteLine("""
                razorgraph-mcp [--http [--port N] [--auth require|report|off]
                               [--auth-token-command "<command line>"]] [--print-url]

                  --http       Serve over HTTP instead of stdio, for the development loop:
                               an HTTP server survives being killed and rebuilt mid-session,
                               so a rebuild no longer costs a session restart.
                  --port       HTTP port. Defaults to 7718. Starting a second server on a port
                               already serving razorgraph is a no-op, not an error: sessions
                               share one HTTP server, and with it one warm graph registry.
                  --auth       The bearer check over HTTP. require (the default) answers 401 to
                               any request without the token; report lets every request through
                               and counts what it carried; off checks nothing. /health is never
                               checked, and shows the mode and the counts.
                  --auth-token-command
                               The command whose trimmed stdout is the token: an executable,
                               found through PATH, then its arguments, run without a shell. Run
                               at startup and again at most every 30 s when a request's token
                               does not match, so a rotated token is picked up. Required with
                               --auth require.
                  --print-url  Print the URL and exit, to write client config without starting
                               anything.
                """);
            return 0;
    }
}

// Before anything else runs: a server that could only ever answer 401 is refused, not started.
if (http && HttpAuth.StartupError(authMode, authTokenCommand) is string authError)
{
    Console.Error.WriteLine($"razorgraph-mcp will not start: {authError}");
    return 2;
}

// The environment a fresh sign-in would give this server, not the launching terminal's. First,
// before anything reads configuration or registers MSBuild: MSBuild promotes environment
// variables to global properties, so a Platform inherited from a developer prompt would shape
// every graph this server builds, and MSBuild's build host inherits whatever is here.
// RAZORGRAPH_* survives, so the server's own settings still arrive.
// RAZORGRAPH_KEEP_ENVIRONMENT=1 keeps the inherited environment instead. Both transports: a
// client's env block contaminates a graph as surely as a terminal's, and a build that needs a
// particular platform asks for it as a parameter. See ServerEnvironment.Apply.
ServerEnvironment.Apply();

if (port == 0)
{
    port = DefaultPort;
}

if (printUrl)
{
    Console.Out.WriteLine($"http://127.0.0.1:{port}/");
    return 0;
}

if (http)
{
    // An HTTP server is shared infrastructure, not a per-session child process: every session
    // dials the same one. A second start is therefore usually redundant rather than wrong, and
    // crashing with an unhandled AddressInUseException serves nobody.
    (Health.PortState state, string? version) = await Health.ProbeAsync(port);

    if (state == Health.PortState.AlreadyServing)
    {
        // "running", not "serving": the one there may still be starting, reading its token.
        Console.Error.WriteLine(
            $"razorgraph-mcp {version} is already running on http://127.0.0.1:{port}/ -- nothing to do.");

        return 0;
    }

    Func<string?> tokenProvider;
    try
    {
        tokenProvider = HttpAuth.CreateProvider(authTokenCommand);
    }
    catch (FormatException ex)
    {
        Console.Error.WriteLine($"razorgraph-mcp will not start: {HttpAuth.TokenCommandOption}: {ex.Message}");
        return 2;
    }

    // Services, the loopback guard, the bearer check, the MCP endpoint and /health: see
    // HttpTransport.Build. Disposed on the way out, which stops the bearer check's consumer.
    await using WebApplication app = HttpTransport.Build(port, authMode, tokenProvider);

    try
    {
        await app.StartAsync();
    }
    catch (IOException ex) when (ex.InnerException is Microsoft.AspNetCore.Connections.AddressInUseException)
    {
        // Lost a race with another instance that probed free at the same moment. Re-probe: if a
        // razorgraph-mcp is there now, the job is done and this is not a failure.
        (Health.PortState raced, string? racedVersion) = await Health.ProbeAsync(port);
        if (raced == Health.PortState.AlreadyServing)
        {
            Console.Error.WriteLine(
                $"razorgraph-mcp {racedVersion} won the race for http://127.0.0.1:{port}/ -- nothing to do.");

            return 0;
        }

        Console.Error.WriteLine(
            $"Port {port} is in use by something that is not a razorgraph-mcp. Pass --port to run " +
            $"elsewhere.{Environment.NewLine}{ex.Message}");

        return 3;
    }

    // Printed AFTER binding. Announcing it is listening and then dying of AddressInUseException
    // is the tool asserting something it has not established.
    // Loopback only: this transport is a local development loop, not a network service.
    Console.Error.WriteLine($"razorgraph-mcp listening on http://127.0.0.1:{port}/");

    // The token command runs once, on a background task begun when the port was bound, while
    // the authenticator is built; until then the server answers 503. Wait for it, and say now if
    // it failed, rather than leaving it for the first 401 to reveal. The error carries the
    // command's stderr, never its stdout.
    BearerAuthenticator bearer = await app.Services.GetRequiredService<TransportReadiness>().Ready;
    BearerAuthSnapshot auth = bearer.Snapshot();
    Console.Error.WriteLine(
        $"Bearer check: {auth.Mode}" +
        (authMode == BearerMode.Off ? "." : auth.TokenConfigured ? ", token configured." : ", NO token configured.") +
        (auth.ProviderError is null ? string.Empty : $" Reading it failed: {auth.ProviderError}"));

    await app.WaitForShutdownAsync();

    return 0;
}

var host = Host.CreateApplicationBuilder(args);

// stdout carries the MCP protocol; everything else must go to stderr.
host.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

host.Services.AddSingleton<GraphStore>();
host.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await host.Build().RunAsync();

return 0;
