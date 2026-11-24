namespace RazorGraph.Tests;

using System.Net.Http.Headers;
using System.Text;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using RazorGraph.Mcp;

/// <summary>
/// HttpTransport.Build started on an ephemeral loopback port, with a client to dial it. The
/// pipeline tests share it so every one of them runs the server's real pipeline.
/// </summary>
internal sealed class RunningTransport : IAsyncDisposable
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"pipeline-test","version":"0"}}}""";

    private RunningTransport(WebApplication app)
    {
        App = app;
    }

    public WebApplication App { get; }

    public HttpClient Client { get; } = new() { Timeout = TimeSpan.FromSeconds(30) };

    public string BaseUrl => App.Urls.First().TrimEnd('/');

    public int Port => new Uri(BaseUrl).Port;

    /// <summary>The bearer check. Read it once <see cref="Ready"/> has completed: before that, it blocks on the provider.</summary>
    public BearerAuthenticator Bearer => App.Services.GetRequiredService<BearerAuthenticator>();

    /// <summary>Completes when the server has built its bearer check, and has stopped answering 503.</summary>
    public Task<BearerAuthenticator> Ready => App.Services.GetRequiredService<TransportReadiness>().Ready;

    /// <summary>
    /// Builds and binds the server. By default waits until it is ready, as the bearer tests need;
    /// pass waitUntilReady false to catch it still starting, with a provider that blocks.
    /// </summary>
    public static async Task<RunningTransport> StartAsync(
        BearerMode mode, Func<string?> tokenProvider, bool waitUntilReady = true)
    {
        WebApplication app = HttpTransport.Build(0, mode, tokenProvider);
        await app.StartAsync();
        RunningTransport running = new(app);
        if (waitUntilReady)
        {
            await running.Ready.WaitAsync(TimeSpan.FromSeconds(30));
        }

        return running;
    }

    /// <summary>An MCP initialize POST, with a bearer token when one is given.</summary>
    public HttpRequestMessage InitializeRequest(string? bearer = null)
    {
        HttpRequestMessage request = new(HttpMethod.Post, BaseUrl + "/")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (bearer is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        }

        return request;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await App.StopAsync();
        await App.DisposeAsync();
    }
}
