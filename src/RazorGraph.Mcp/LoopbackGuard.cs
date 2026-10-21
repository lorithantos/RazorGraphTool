using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace RazorGraph.Mcp;

/// <summary>Why the guard refused a request: the status to answer with and a plain-text reason.</summary>
internal sealed record GuardRejection(int StatusCode, string Message);

/// <summary>
/// Refuses any HTTP request that did not come from this machine's own loopback, before anything
/// else in the pipeline answers it.
/// </summary>
/// <remarks>
/// The HTTP transport binds 127.0.0.1, but binding loopback does not stop a browser from reaching
/// it: a page on rebind.evil.example can re-point its own name at 127.0.0.1 (DNS rebinding) and
/// then talk to this port as "same origin", or simply POST to it cross-site. A probe on
/// 2026-10-21 sent a forged Host and Origin and MCP initialize answered 200. Three checks close
/// that, in this order:
///
///   Host          must be 127.0.0.1, localhost or [::1], any port         -> 400 otherwise
///   Origin        absent is fine (Claude Code and Invoke-McpTool.ps1 send
///                 none); present, it must be http(s) on one of those names -> 403 otherwise
///   Content-Type  every POST must be application/json (parameters allowed),
///                 so a browser cannot send a no-preflight "simple" POST    -> 415 otherwise
///
/// ASP.NET Core's HostFiltering was considered for the Host check and not used: it answers a
/// fixed HTML page that does not name the Host it refused. The MCP SDK (ModelContextProtocol
/// .AspNetCore 1.4.1) offers no origin validation on HttpServerTransportOptions.
///
/// janet-mcp carries the same three checks with the same names and status codes. They are kept
/// in step by hand, not shared: RazorGraphTool does not depend on Janet.
/// </remarks>
internal static class LoopbackGuard
{
    /// <summary>The host names served. Compared case-insensitively, with any port.</summary>
    internal static readonly IReadOnlyList<string> LoopbackNames = ["127.0.0.1", "localhost", "[::1]"];

    /// <summary>
    /// The guard's whole decision. Null means the request may proceed.
    /// </summary>
    /// <param name="origin">The Origin header, or null when the request carried none.</param>
    internal static GuardRejection? Evaluate(string method, string? host, string? origin, string? contentType)
    {
        if (!IsLoopbackHost(host))
        {
            return new GuardRejection(
                StatusCodes.Status400BadRequest,
                $"Rejected Host '{host}': this server answers only 127.0.0.1, localhost or [::1].\n");
        }

        if (!IsAllowedOrigin(origin))
        {
            return new GuardRejection(
                StatusCodes.Status403Forbidden,
                $"Rejected Origin '{origin}': only a loopback origin (127.0.0.1, localhost or [::1]) may call this server.\n");
        }

        if (string.Equals(method, "POST", StringComparison.OrdinalIgnoreCase) && !IsJsonContentType(contentType))
        {
            return new GuardRejection(
                StatusCodes.Status415UnsupportedMediaType,
                $"Rejected Content-Type '{contentType ?? "(none)"}': a POST must be application/json.\n");
        }

        return null;
    }

    /// <summary>
    /// True when a Host header value names a loopback host: one of <see cref="LoopbackNames"/>,
    /// optionally followed by :port. Nothing is resolved -- 127.0.0.1.nip.io is a name, not 127.0.0.1.
    /// </summary>
    internal static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        string name;
        string? port = null;

        if (host[0] == '[')
        {
            int close = host.IndexOf(']');
            if (close < 0)
            {
                return false;
            }

            name = host[..(close + 1)];
            string rest = host[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }

                port = rest[1..];
            }
        }
        else
        {
            int colon = host.IndexOf(':');
            if (colon < 0)
            {
                name = host;
            }
            else
            {
                name = host[..colon];
                port = host[(colon + 1)..];
            }
        }

        if (port is not null && !IsPort(port))
        {
            return false;
        }

        foreach (string allowed in LoopbackNames)
        {
            if (string.Equals(name, allowed, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when a request may carry this Origin: none at all, or http(s)://loopback[:port]
    /// exactly. "null" (a sandboxed or file: page) is refused, as is anything with a path.
    /// </summary>
    internal static bool IsAllowedOrigin(string? origin)
    {
        if (origin is null)
        {
            return true;
        }

        string rest;
        if (origin.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
        {
            rest = origin["http://".Length..];
        }
        else if (origin.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            rest = origin["https://".Length..];
        }
        else
        {
            return false;
        }

        return IsLoopbackHost(rest);
    }

    /// <summary>True for application/json, with or without parameters such as charset.</summary>
    internal static bool IsJsonContentType(string? contentType)
    {
        if (string.IsNullOrEmpty(contentType))
        {
            return false;
        }

        int semicolon = contentType.IndexOf(';');
        string mediaType = (semicolon < 0 ? contentType : contentType[..semicolon]).Trim();

        return string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Registers the guard. Call it before anything that can answer a request, so that no
    /// endpoint -- /health included -- is reached by a request the guard would refuse.
    /// </summary>
    internal static IApplicationBuilder UseLoopbackGuard(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            HttpRequest request = context.Request;
            GuardRejection? rejection = Evaluate(
                request.Method,
                request.Headers.Host.ToString(),
                request.Headers.Origin.Count == 0 ? null : request.Headers.Origin.ToString(),
                request.ContentType);

            if (rejection is null)
            {
                await next(context);
                return;
            }

            context.Response.StatusCode = rejection.StatusCode;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(rejection.Message);
        });

    private static bool IsPort(string port) =>
        port.Length is >= 1 and <= 5 && port.All(char.IsAsciiDigit);
}
