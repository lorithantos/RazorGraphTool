namespace RazorGraph.Tests;

using System.Net;
using System.Text;
using Janet.Mcp.Security;
using Xunit;

/// <summary>
/// The guard in the real pipeline: HttpTransport.Build on an ephemeral loopback port, dialled
/// over a socket. Kestrel rather than TestServer, because the repo carries no TestServer
/// dependency and Kestrel comes with the framework reference the server already has.
/// </summary>
/// <remarks>
/// The guard's pure decisions (which Hosts, Origins and Content-Types pass) are tested where the
/// guard now lives, in Janet.Shared's Janet.Mcp.Security. These tests prove this server installs
/// it, and installs it before the bearer check: the server runs in Require mode, and a forged Host
/// is refused for what it is (400) rather than for its missing token (401).
/// </remarks>
public sealed class LoopbackGuardPipelineTests : IAsyncLifetime
{
    private const string Token = "guard-test-token";

    private RunningTransport? _server;

    private RunningTransport Server => _server!;

    public async Task InitializeAsync() =>
        _server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);

    public async Task DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task AForgedHostIsRejectedBeforeMcpAnswers()
    {
        // No Authorization either: the guard must answer before the bearer check would say 401.
        using HttpRequestMessage request = Server.InitializeRequest();
        // No Origin: the Host alone must be enough to refuse it, as it is for a rebinding page
        // whose fetch the browser treats as same-origin.
        request.Headers.Host = "rebind.evil.example";

        using HttpResponseMessage response = await Server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rebind.evil.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackInitializePassesTheGuard()
    {
        using HttpRequestMessage request = Server.InitializeRequest(Token);

        using HttpResponseMessage response = await Server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AForeignOriginIsRejectedEvenOnHealth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, Server.BaseUrl + "/health");
        request.Headers.TryAddWithoutValidation("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await Server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <remarks>
    /// Aimed at /health, not the MCP endpoint, on purpose: the MCP SDK already answers 415 to a
    /// text/plain POST on its own endpoint, so a test there passes with the guard's check removed.
    /// Without the guard /health answers a POST with 405.
    /// </remarks>
    [Fact]
    public async Task ATextPlainPostIsRejectedOnEveryPath()
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Server.BaseUrl + "/health")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };

        using HttpResponseMessage response = await Server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
