namespace RazorGraph.Tests;

using System.Net;
using System.Text.Json;
using Janet.Mcp.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The HTTP server binds before it reads its bearer token, and answers 503 until it has. Each test
/// holds the token provider on a gate, so "still starting" is a state the test chooses rather than
/// a window it hopes to land in.
/// </summary>
public sealed class TransportStartupTests
{
    private const string Token = "startup-test-token-8d2e";

    /// <summary>Long enough for a loaded host; a provider held past it gives up and the test fails.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task WhileTheTokenIsReadHealthAnswers503StartingWithTheServerName()
    {
        using ManualResetEventSlim gate = new();
        await using RunningTransport server = await StartHeldAsync(gate, () => Token);
        try
        {
            using HttpResponseMessage health = await server.Client.GetAsync(server.BaseUrl + "/health");
            using JsonDocument json = JsonDocument.Parse(await health.Content.ReadAsStringAsync());

            Assert.Equal(HttpStatusCode.ServiceUnavailable, health.StatusCode);
            Assert.Equal(Health.Name, json.RootElement.GetProperty("server").GetString());
            Assert.False(string.IsNullOrEmpty(json.RootElement.GetProperty("version").GetString()));
            Assert.Equal("starting", json.RootElement.GetProperty("state").GetString());
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task WhileTheTokenIsReadTheMcpEndpointSaysRetryRatherThan401()
    {
        using ManualResetEventSlim gate = new();
        await using RunningTransport server = await StartHeldAsync(gate, () => Token);
        try
        {
            using HttpResponseMessage missing = await server.Client.SendAsync(server.InitializeRequest());
            using HttpResponseMessage right = await server.Client.SendAsync(server.InitializeRequest(Token));

            foreach (HttpResponseMessage response in new[] { missing, right })
            {
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal(TimeSpan.FromSeconds(1), response.Headers.RetryAfter?.Delta);
            }

            Assert.Contains("starting", await missing.Content.ReadAsStringAsync());
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task TheStartupProbeRecognisesAServerStillStarting()
    {
        using ManualResetEventSlim gate = new();
        await using RunningTransport server = await StartHeldAsync(gate, () => Token);
        try
        {
            (Health.PortState state, string? version) = await Health.ProbeAsync(server.Port, Patience);

            Assert.Equal(Health.PortState.AlreadyServing, state);
            Assert.NotNull(version);
        }
        finally
        {
            gate.Set();
        }
    }

    [Fact]
    public async Task OnceTheTokenIsReadHealthIsReadyAndTheBearerCheckDecides()
    {
        using ManualResetEventSlim gate = new();
        await using RunningTransport server = await StartHeldAsync(gate, () => Token);
        gate.Set();
        await server.Ready.WaitAsync(Patience);

        using HttpResponseMessage health = await server.Client.GetAsync(server.BaseUrl + "/health");
        using JsonDocument json = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        using HttpResponseMessage missing = await server.Client.SendAsync(server.InitializeRequest());
        using HttpResponseMessage right = await server.Client.SendAsync(server.InitializeRequest(Token));

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
        Assert.True(json.RootElement.GetProperty("auth").GetProperty("tokenConfigured").GetBoolean());
        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
    }

    [Fact]
    public async Task AProviderThatThrowsStillEndsReadyWithItsError()
    {
        using ManualResetEventSlim gate = new();
        await using RunningTransport server = await StartHeldAsync(
            gate,
            () => throw new InvalidOperationException("'token-tool' exited with code 3. stderr: vault is locked"));

        using (HttpResponseMessage starting = await server.Client.GetAsync(server.BaseUrl + "/health"))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, starting.StatusCode);
        }

        gate.Set();
        await server.Ready.WaitAsync(Patience);

        using HttpResponseMessage health = await server.Client.GetAsync(server.BaseUrl + "/health");
        using JsonDocument json = JsonDocument.Parse(await health.Content.ReadAsStringAsync());
        JsonElement auth = json.RootElement.GetProperty("auth");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal("ready", json.RootElement.GetProperty("state").GetString());
        Assert.False(auth.GetProperty("tokenConfigured").GetBoolean());
        Assert.Contains("vault is locked", auth.GetProperty("providerError").GetString());
    }

    /// <remarks>
    /// The probe reads a body whatever its status, so it must still tell a razorgraph-mcp from
    /// someone else's /health that happens to be JSON and 503.
    /// </remarks>
    [Fact]
    public async Task TheStartupProbeCallsSomeoneElses503Free()
    {
        WebApplication other = WebApplication.CreateBuilder().Build();
        other.MapGet("/health", () => Results.Json(
            new { server = "someone-else", version = "9.9.9", state = "starting" },
            statusCode: StatusCodes.Status503ServiceUnavailable));
        other.Urls.Add("http://127.0.0.1:0");
        await other.StartAsync();
        try
        {
            int port = new Uri(other.Urls.First()).Port;

            (Health.PortState state, string? version) = await Health.ProbeAsync(port, Patience);

            Assert.Equal(Health.PortState.Free, state);
            Assert.Null(version);
        }
        finally
        {
            await other.StopAsync();
            await other.DisposeAsync();
        }
    }

    /// <summary>A server in Require mode whose provider waits for the gate before answering.</summary>
    private static Task<RunningTransport> StartHeldAsync(ManualResetEventSlim gate, Func<string?> provider) =>
        RunningTransport.StartAsync(
            BearerMode.Require,
            () =>
            {
                gate.Wait(Patience);
                return provider();
            },
            waitUntilReady: false);
}
