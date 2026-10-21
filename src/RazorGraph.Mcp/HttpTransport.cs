using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace RazorGraph.Mcp;

/// <summary>
/// The HTTP transport's pipeline, built in one place so the server and its tests run the same one.
/// </summary>
internal static class HttpTransport
{
    /// <summary>
    /// Builds, but does not start, the HTTP server for a loopback port. Port 0 asks Kestrel for a
    /// free one, which is what the tests use; read app.Urls after StartAsync for the real port.
    /// </summary>
    internal static WebApplication Build(int port)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<GraphStore>();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        WebApplication app = builder.Build();

        // First, before any endpoint: nothing -- /health included -- answers a request from a
        // non-loopback Host or Origin, or a POST that is not JSON. See LoopbackGuard.
        app.UseLoopbackGuard();

        app.MapMcp();

        // Lets another instance tell "a razorgraph-mcp" from "someone else's port".
        app.MapGet("/health", () => new HealthReport(
            Health.Name,
            typeof(HttpTransport).Assembly.GetName().Version?.ToString() ?? "0.0.0"));

        app.Urls.Add($"http://127.0.0.1:{port}");

        return app;
    }
}
