namespace RazorGraph.Tests;

using System.Net;
using System.Text.Json;
using Janet.Mcp.Security;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The bearer check in the server's real pipeline, in each mode, with a test provider standing in
/// for --auth-token-command. Each test starts its own server, because the counters are per server.
/// </summary>
public sealed class BearerPipelineTests
{
    private const string Token = "pipeline-test-token-5f1c";

    [Fact]
    public async Task RequireRefusesAMissingOrWrongTokenAndAdmitsTheRightOne()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);

        using HttpResponseMessage missing = await server.Client.SendAsync(server.InitializeRequest());
        using HttpResponseMessage wrong = await server.Client.SendAsync(server.InitializeRequest("not-the-token"));
        using HttpResponseMessage right = await server.Client.SendAsync(server.InitializeRequest(Token));

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);
        Assert.DoesNotContain(Token, await wrong.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task TheRefusalTellsTheClientToGetItsHeaderFromJanet()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);

        using HttpResponseMessage missing = await server.Client.SendAsync(server.InitializeRequest());
        string body = await missing.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, missing.StatusCode);
        Assert.EndsWith(" Run `janet mcp-headers` to print the header to send.", body.TrimEnd());
    }

    [Fact]
    public async Task RequireStillAnswersHealthWithoutAToken()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);

        using HttpResponseMessage health = await server.Client.GetAsync(server.BaseUrl + "/health");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    [Fact]
    public async Task RequireWithNoTokenRefusesEvenAGuess()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => null);

        using HttpResponseMessage response = await server.Client.SendAsync(server.InitializeRequest(Token));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(1, server.Bearer.Snapshot().Unconfigured);
    }

    [Fact]
    public async Task ReportLetsEveryRequestThroughAndCountsIt()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Report, () => Token);

        using HttpResponseMessage missing = await server.Client.SendAsync(server.InitializeRequest());
        using HttpResponseMessage wrong = await server.Client.SendAsync(server.InitializeRequest("not-the-token"));
        using HttpResponseMessage right = await server.Client.SendAsync(server.InitializeRequest(Token));

        Assert.Equal(HttpStatusCode.OK, missing.StatusCode);
        Assert.Equal(HttpStatusCode.OK, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.OK, right.StatusCode);

        BearerAuthSnapshot snapshot = server.Bearer.Snapshot();
        Assert.Equal((1L, 1L, 1L), (snapshot.Ok, snapshot.Missing, snapshot.Wrong));
    }

    [Fact]
    public async Task OffChecksNothingAndNeverAsksTheProvider()
    {
        int asked = 0;
        await using RunningTransport server = await RunningTransport.StartAsync(
            BearerMode.Off,
            () =>
            {
                Interlocked.Increment(ref asked);
                return Token;
            });

        using HttpResponseMessage response = await server.Client.SendAsync(server.InitializeRequest());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(0, Volatile.Read(ref asked));
        BearerAuthSnapshot snapshot = server.Bearer.Snapshot();
        Assert.Equal("Off", snapshot.Mode);
        Assert.Equal(0L, snapshot.Ok + snapshot.Missing + snapshot.Wrong + snapshot.Unconfigured);
    }

    [Fact]
    public async Task HealthShowsTheModeAndCountersButNeverTheToken()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);
        using (await server.Client.SendAsync(server.InitializeRequest(Token)))
        using (await server.Client.SendAsync(server.InitializeRequest()))
        using (await server.Client.SendAsync(server.InitializeRequest("not-the-token")))
        {
        }

        string body = await server.Client.GetStringAsync(server.BaseUrl + "/health");

        Assert.DoesNotContain(Token, body);
        Assert.DoesNotContain("not-the-token", body);
        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement auth = json.RootElement.GetProperty("auth");
        Assert.Equal("Require", auth.GetProperty("mode").GetString());
        Assert.True(auth.GetProperty("tokenConfigured").GetBoolean());
        Assert.Equal(1, auth.GetProperty("ok").GetInt64());
        Assert.Equal(1, auth.GetProperty("missing").GetInt64());
        Assert.Equal(1, auth.GetProperty("wrong").GetInt64());
        Assert.Equal(0, auth.GetProperty("unconfigured").GetInt64());
        Assert.Equal(JsonValueKind.Null, auth.GetProperty("providerError").ValueKind);
    }

    [Fact]
    public async Task HealthShowsWhyTheProviderFailed()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(
            BearerMode.Require,
            () => throw new InvalidOperationException("'token-tool' exited with code 3. stderr: vault is locked"));

        string body = await server.Client.GetStringAsync(server.BaseUrl + "/health");

        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement auth = json.RootElement.GetProperty("auth");
        Assert.False(auth.GetProperty("tokenConfigured").GetBoolean());
        Assert.Contains("vault is locked", auth.GetProperty("providerError").GetString());
    }

    /// <remarks>
    /// The /health record gained a field; a second server's start-up probe must still recognise
    /// the first, or it would try to bind, lose, and report the port as someone else's. The probe
    /// waits as long as a loaded test host needs: the server's own 500 ms is a start-up budget,
    /// and missing it here said nothing about the record.
    /// </remarks>
    [Fact]
    public async Task TheStartupProbeStillRecognisesARunningServer()
    {
        await using RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);

        (Health.PortState state, string? version) = await Health.ProbeAsync(server.Port, TimeSpan.FromSeconds(30));

        Assert.Equal(Health.PortState.AlreadyServing, state);
        Assert.NotNull(version);
    }

    [Fact]
    public async Task DisposingTheAppDisposesTheAuthenticator()
    {
        RunningTransport server = await RunningTransport.StartAsync(BearerMode.Require, () => Token);
        BearerAuthenticator bearer = server.Bearer;

        await server.DisposeAsync();

        // A wrong token needs a refresh, and a disposed authenticator has no consumer to ask.
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await bearer.AuthenticateAsync("Bearer not-the-token"));
    }
}
