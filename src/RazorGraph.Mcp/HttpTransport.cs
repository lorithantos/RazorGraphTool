using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
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

    /// <summary>The plain-text 503 body for any route but /health while the server is starting.</summary>
    internal const string StartingMessage =
        "razorgraph-mcp is starting: it is still reading its bearer token. Retry in a moment.";

    /// <summary>
    /// Builds, but does not start, the HTTP server for a loopback port. Port 0 asks Kestrel for a
    /// free one, which is what the tests use; read app.Urls after StartAsync for the real port.
    /// </summary>
    /// <remarks>
    /// Starting the app binds the port and answers at once. The bearer check is built afterwards,
    /// on a background task the app's Started event begins (see <see cref="TransportReadiness"/>);
    /// until it is, /health answers 503 "starting" and every other route 503 with Retry-After.
    /// Await <see cref="TransportReadiness.Ready"/> from the app's services for the check itself.
    /// </remarks>
    /// <param name="port">The loopback port, or 0 for a free one.</param>
    /// <param name="mode">How the bearer token is treated.</param>
    /// <param name="tokenProvider">
    /// Returns the token requests must carry, or null for none. Asked once when the authenticator
    /// is built, after the port is bound, and afterwards only by its refresh consumer.
    /// </param>
    internal static WebApplication Build(int port, BearerMode mode, Func<string?> tokenProvider)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<GraphStore>();

        // Registered through a factory so the container owns it: disposing the app disposes the
        // authenticator, which stops its refresh consumer. Nothing resolves it here: constructing
        // it runs the token provider, which waits for TransportReadiness, after the bind.
        builder.Services.AddSingleton(_ => new BearerAuthenticator(mode, tokenProvider, headerHint: HeaderHint));
        builder.Services.AddSingleton<TransportReadiness>();
        builder.Services.AddMcpServer().WithHttpTransport().WithToolsFromAssembly();

        WebApplication app = builder.Build();
        TransportReadiness readiness = app.Services.GetRequiredService<TransportReadiness>();

        // Started fires at the end of StartAsync, once Kestrel has bound, so the port answers
        // before the token provider runs. A failed bind never fires it, and never runs the provider.
        app.Lifetime.ApplicationStarted.Register(readiness.Begin);

        // First, before any endpoint, one of two branches, each with the loopback guard at its head
        // (a non-loopback Host or Origin, or a POST that is not JSON, is refused on every path,
        // /health included):
        //   starting: guard, then 503 for everything but /health;
        //   ready:    guard, then the bearer check, which exempts /health (UseMcpSecurity).
        // The bearer check cannot come first while starting: it has no token to judge against, and
        // would answer 401 to a client that only needs to wait.
        IApplicationBuilder pipeline = app;
        app.Use(next =>
        {
            IApplicationBuilder starting = pipeline.New();
            starting.UseLoopbackGuard();
            starting.Use(AnswerStartingAsync);
            starting.Run(next);
            RequestDelegate whileStarting = starting.Build();

            Lazy<RequestDelegate> whenReady = new(() =>
            {
                IApplicationBuilder ready = pipeline.New();
                ready.UseMcpSecurity(readiness.Ready.Result);
                ready.Run(next);
                return ready.Build();
            });

            return context => readiness.IsReady ? whenReady.Value(context) : whileStarting(context);
        });

        app.MapMcp();

        // Lets another instance tell "a razorgraph-mcp" from "someone else's port", and shows the
        // bearer check's mode and counters. The snapshot carries no token. While starting it is
        // 503 with the same server and version, so a second instance's probe still recognises it.
        string version = typeof(HttpTransport).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        app.MapGet("/health", () => readiness.IsReady
            ? Results.Json(new HealthReport(Health.Name, version, readiness.Ready.Result.Snapshot(), Health.Ready))
            : Results.Json(
                new HealthReport(Health.Name, version, Auth: null, Health.Starting),
                statusCode: StatusCodes.Status503ServiceUnavailable));

        app.Urls.Add($"http://127.0.0.1:{port}");

        return app;
    }

    /// <summary>
    /// The starting branch's answer: /health goes on to its endpoint, which reports "starting";
    /// anything else is told to retry. Plain text with nosniff, like the guard's and the bearer
    /// check's refusals.
    /// </summary>
    private static Task AnswerStartingAsync(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.Equals(BearerAuthenticator.ExemptPath, StringComparison.OrdinalIgnoreCase))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.Headers.RetryAfter = "1";
        context.Response.ContentType = "text/plain; charset=utf-8";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        return context.Response.WriteAsync(StartingMessage + Environment.NewLine);
    }
}
