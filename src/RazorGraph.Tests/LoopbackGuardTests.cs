namespace RazorGraph.Tests;

using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The HTTP transport's DNS-rebinding and cross-site guard, as pure decisions.
/// </summary>
/// <remarks>
/// janet-mcp carries the same three checks with the same names and status codes (400 Host,
/// 403 Origin, 415 Content-Type); keep the two in step by hand.
/// </remarks>
public class LoopbackGuardTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("127.0.0.1:7718")]
    [InlineData("localhost")]
    [InlineData("localhost:7718")]
    [InlineData("LOCALHOST:7718")]
    [InlineData("LocalHost")]
    [InlineData("[::1]")]
    [InlineData("[::1]:7718")]
    public void LoopbackHostsAreAccepted(string host) =>
        Assert.True(LoopbackGuard.IsLoopbackHost(host));

    [Theory]
    [InlineData("rebind.evil.example")]
    [InlineData("rebind.evil.example:7718")]
    [InlineData("localhost.evil.example")]
    [InlineData("localhost.evil.example:7718")]
    [InlineData("127.0.0.1.nip.io")]
    [InlineData("127.0.0.1.nip.io:7718")]
    [InlineData("127.0.0.2")]
    [InlineData("127.0.0.2:7718")]
    [InlineData("::1")]
    [InlineData("[::1")]
    [InlineData("[::2]:7718")]
    [InlineData("[::1]x")]
    [InlineData("localhost:")]
    [InlineData("localhost:abc")]
    [InlineData("localhost:7718:1")]
    [InlineData("localhost/")]
    [InlineData("")]
    [InlineData(null)]
    public void NonLoopbackHostsAreRefused(string? host) =>
        Assert.False(LoopbackGuard.IsLoopbackHost(host));

    [Fact]
    public void MissingOriginIsAllowed() =>
        Assert.True(LoopbackGuard.IsAllowedOrigin(null));

    [Theory]
    [InlineData("http://127.0.0.1:7718")]
    [InlineData("http://localhost")]
    [InlineData("http://localhost:7718")]
    [InlineData("HTTP://LOCALHOST:7718")]
    [InlineData("http://[::1]:7718")]
    [InlineData("https://localhost")]
    [InlineData("https://127.0.0.1:7718")]
    [InlineData("https://[::1]")]
    public void LoopbackOriginsAreAllowed(string origin) =>
        Assert.True(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    [InlineData("http://rebind.evil.example")]
    [InlineData("http://localhost.evil.example")]
    [InlineData("http://127.0.0.1.nip.io")]
    [InlineData("http://127.0.0.2:7718")]
    [InlineData("http://localhost/")]
    [InlineData("http://user@localhost")]
    [InlineData("file://localhost")]
    [InlineData("localhost")]
    [InlineData("http://localhost, http://rebind.evil.example")]
    public void ForeignOriginsAreRefused(string origin) =>
        Assert.False(LoopbackGuard.IsAllowedOrigin(origin));

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON;charset=UTF-8")]
    [InlineData(" application/json ")]
    public void JsonContentTypesAreAccepted(string contentType) =>
        Assert.True(LoopbackGuard.IsJsonContentType(contentType));

    [Theory]
    [InlineData("text/plain")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData("application/jsonp")]
    [InlineData("")]
    [InlineData(null)]
    public void OtherContentTypesAreRefused(string? contentType) =>
        Assert.False(LoopbackGuard.IsJsonContentType(contentType));

    [Fact]
    public void ALoopbackJsonPostWithNoOriginPasses() =>
        Assert.Null(LoopbackGuard.Evaluate("POST", "127.0.0.1:7718", null, "application/json; charset=utf-8"));

    [Fact]
    public void AGetNeedsNoContentType() =>
        Assert.Null(LoopbackGuard.Evaluate("GET", "localhost:7718", null, null));

    [Fact]
    public void AForgedHostIsRefusedWith400NamingIt()
    {
        GuardRejection? rejection = LoopbackGuard.Evaluate(
            "POST", "rebind.evil.example", "http://rebind.evil.example", "application/json");

        Assert.NotNull(rejection);
        Assert.Equal(400, rejection.StatusCode);
        Assert.Contains("rebind.evil.example", rejection.Message);
    }

    [Fact]
    public void AnEmptyHostIsRefusedWith400() =>
        Assert.Equal(400, LoopbackGuard.Evaluate("GET", "", null, null)?.StatusCode);

    [Fact]
    public void AForeignOriginIsRefusedWith403NamingIt()
    {
        GuardRejection? rejection = LoopbackGuard.Evaluate(
            "POST", "127.0.0.1:7718", "http://rebind.evil.example", "application/json");

        Assert.NotNull(rejection);
        Assert.Equal(403, rejection.StatusCode);
        Assert.Contains("http://rebind.evil.example", rejection.Message);
    }

    [Fact]
    public void ANullOriginIsRefusedWith403() =>
        Assert.Equal(403, LoopbackGuard.Evaluate("GET", "127.0.0.1:7718", "null", null)?.StatusCode);

    [Fact]
    public void AnHttpsLoopbackOriginPasses() =>
        Assert.Null(LoopbackGuard.Evaluate("POST", "localhost:7718", "https://localhost:7718", "application/json"));

    [Fact]
    public void ATextPlainPostIsRefusedWith415() =>
        Assert.Equal(415, LoopbackGuard.Evaluate("POST", "127.0.0.1:7718", null, "text/plain")?.StatusCode);

    [Fact]
    public void APostWithNoContentTypeIsRefusedWith415() =>
        Assert.Equal(415, LoopbackGuard.Evaluate("post", "127.0.0.1:7718", null, null)?.StatusCode);
}

/// <summary>
/// The guard in the real pipeline: HttpTransport.Build on an ephemeral loopback port, dialled
/// over a socket. Kestrel rather than TestServer, because the repo carries no TestServer
/// dependency and Kestrel comes with the framework reference the server already has.
/// </summary>
public sealed class LoopbackGuardPipelineTests : IAsyncLifetime
{
    private const string Initialize =
        """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"guard-test","version":"0"}}}""";

    private WebApplication? _app;
    private readonly HttpClient _client = new() { Timeout = TimeSpan.FromSeconds(30) };

    private string BaseUrl => _app!.Urls.First().TrimEnd('/');

    public async Task InitializeAsync()
    {
        _app = HttpTransport.Build(0);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    private HttpRequestMessage InitializeRequest()
    {
        HttpRequestMessage request = new(HttpMethod.Post, BaseUrl + "/")
        {
            Content = new StringContent(Initialize, Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    [Fact]
    public async Task AForgedHostIsRejectedBeforeMcpAnswers()
    {
        using HttpRequestMessage request = InitializeRequest();
        // No Origin: the Host alone must be enough to refuse it, as it is for a rebinding page
        // whose fetch the browser treats as same-origin.
        request.Headers.Host = "rebind.evil.example";

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("rebind.evil.example", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ALoopbackInitializePassesTheGuard()
    {
        using HttpRequestMessage request = InitializeRequest();

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task AForeignOriginIsRejectedEvenOnHealth()
    {
        using HttpRequestMessage request = new(HttpMethod.Get, BaseUrl + "/health");
        request.Headers.TryAddWithoutValidation("Origin", "http://rebind.evil.example");

        using HttpResponseMessage response = await _client.SendAsync(request);

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
        using HttpRequestMessage request = new(HttpMethod.Post, BaseUrl + "/health")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain"),
        };

        using HttpResponseMessage response = await _client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }
}
