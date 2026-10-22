using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace RazorGraph.Mcp;

/// <summary>
/// The HTTP transport's pipeline, built in one place so the server and its tests run the same one.
/// </summary>
internal static class HttpTransport
{
    /// <summary>
    /// The sentence that ends every 401. Janet.Mcp.Security names no tool by default; this server's
    /// clients get their header from janet, the one program that reads the per-machine token, so
    /// the refusal says so.
    /// </summary>
    internal const string HeaderHint = "Run `janet mcp-headers` to print the header to send.";

    /// <summary>
    /// Builds, but does not start, the HTTP server for a loopback port. Port 0 asks Kestrel for a
    /// free one, which is what the tests use; read app.Urls after StartAsync for the real port.
    /// </summary>
    /// <param name="port">The loopback port, or 0 for a free one.</param>
    /// <param name="mode">How the bearer token is treated.</param>
    /// <param name="tokenProvider">
    /// Returns the token requests must carry, or null for none. Asked once here, while the
    /// authenticator is built, and afterwards only by its refresh consumer.
    /// </param>
    internal static WebApplication Build(int port, BearerMode mode, Func<string?> tokenProvider)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<GraphStore>();

        // Registered through a factory so the container owns it: disposing the app disposes the
        // authenticator, which stops its refresh consumer.
        builder.Services.AddSingleton(_ => new BearerAuthenticator(mode, tokenProvider, headerHint: HeaderHint));
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        WebApplication app = builder.Build();
        BearerAuthenticator bearer = app.Services.GetRequiredService<BearerAuthenticator>();

        // First, before any endpoint: the loopback guard (a non-loopback Host or Origin, or a POST
        // that is not JSON, is refused on every path, /health included), then the bearer check,
        // which exempts /health. Both from Janet.Mcp.Security.
        app.UseMcpSecurity(bearer);

        app.MapMcp();

        // Lets another instance tell "a razorgraph-mcp" from "someone else's port", and shows the
        // bearer check's mode and counters. The snapshot carries no token.
        app.MapGet("/health", () => new HealthReport(
            Health.Name,
            typeof(HttpTransport).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            bearer.Snapshot()));

        app.Urls.Add($"http://127.0.0.1:{port}");

        return app;
    }
}
