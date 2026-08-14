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

bool http = false;
bool printUrl = false;
int port = 0;

for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--port" when i + 1 < args.Length: port = int.Parse(args[++i]); break;
        case "--http": http = true; break;
        case "--print-url": printUrl = true; break;
        case "--help":
            Console.Error.WriteLine("""
                razorgraph-mcp [--http [--port N]] [--print-url]

                  --http       Serve over HTTP instead of stdio, for the development loop:
                               an HTTP server survives being killed and rebuilt mid-session,
                               so a rebuild no longer costs a session restart.
                  --port       HTTP port. Defaults to 7718. Starting a second server on a port
                               already serving razorgraph is a no-op, not an error: sessions
                               share one HTTP server, and with it one warm graph registry.
                  --print-url  Print the URL and exit, to write client config without starting
                               anything.
                """);
            return 0;
    }
}

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
        Console.Error.WriteLine(
            $"razorgraph-mcp {version} is already serving on http://127.0.0.1:{port}/ -- nothing to do.");

        return 0;
    }

    WebApplicationBuilder builder = WebApplication.CreateBuilder();
    builder.Services.AddSingleton<GraphStore>();
    builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

    WebApplication app = builder.Build();
    app.MapMcp();

    // Lets another instance tell "a razorgraph-mcp" from "someone else's port".
    app.MapGet("/health", () => new HealthReport(
        Health.Name,
        typeof(Program).Assembly.GetName().Version?.ToString() ?? "0.0.0"));

    app.Urls.Add($"http://127.0.0.1:{port}");

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
