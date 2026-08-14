using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace RazorGraph.Mcp;

/// <summary>What a razorgraph-mcp reports about itself at /health.</summary>
public sealed record HealthReport(
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("version")] string Version);

/// <summary>
/// Decides whether starting an HTTP server on a port is necessary or redundant.
/// </summary>
/// <remarks>
/// Without this, a second session starting its own server crashes with an unhandled
/// AddressInUseException and a CLR exit code -- after already printing that it was listening.
/// Neither the crash nor the claim is useful. An HTTP server is shared infrastructure: a second
/// start against a healthy one is not an error, it is a no-op, and should say so and exit 0.
///
/// Sharing one server is a feature here rather than a tolerated collision. Solution graphs are
/// expensive to build and cheap to hold, so a server outliving the session that built one lets
/// the next session query it for nothing.
/// </remarks>
public static class Health
{
    public const string Name = "razorgraph-mcp";

    public enum PortState
    {
        /// <summary>Nothing recognisable there. Bind it, and let the bind decide.</summary>
        Free,

        /// <summary>A razorgraph-mcp is already serving. Nothing to do.</summary>
        AlreadyServing,
    }

    /// <summary>
    /// Asks who owns a port. Only a valid razorgraph-mcp answer is treated as occupied.
    /// </summary>
    /// <remarks>
    /// Every other outcome -- refused, timed out, garbage, someone else's /health -- reports
    /// Free, because this probe is an optimisation and the BIND is the authority. Treating a
    /// non-answer as "occupied by something foreign" gets it backwards and refuses to start over
    /// a port that is simply empty: connections to a dead loopback port can hang for the full
    /// timeout rather than being refused, which would block every start.
    ///
    /// The cost of guessing Free wrongly is bounded and self-correcting: the bind fails with
    /// AddressInUse, the caller re-probes, and it resolves to a real error or a clean no-op. The
    /// cost of guessing occupied wrongly is a tool that will not start at all.
    /// </remarks>
    public static async Task<(PortState State, string? Version)> ProbeAsync(int port)
    {
        using HttpClient client = new() { Timeout = TimeSpan.FromMilliseconds(500) };

        HealthReport? report;
        try
        {
            report = await client.GetFromJsonAsync<HealthReport>($"http://127.0.0.1:{port}/health");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException
            or NotSupportedException or System.Text.Json.JsonException or UriFormatException)
        {
            return (PortState.Free, null);
        }

        return report is not null && report.Server == Name
            ? (PortState.AlreadyServing, report.Version)
            : (PortState.Free, null);
    }
}
