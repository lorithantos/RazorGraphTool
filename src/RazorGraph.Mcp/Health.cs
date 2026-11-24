using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Janet.Mcp.Security;

namespace RazorGraph.Mcp;

/// <summary>What a razorgraph-mcp reports about itself at /health.</summary>
/// <param name="Server">Always <see cref="Health.Name"/>.</param>
/// <param name="Version">The server's assembly version.</param>
/// <param name="Auth">
/// The bearer check's mode, whether a token is configured, its counters and any provider error.
/// Never the token. Null from a server older than the check, and from one still
/// <see cref="Health.Starting"/>, which has not built the check yet.
/// </param>
/// <param name="State">
/// <see cref="Health.Starting"/> while the server is bound but still reading its bearer token, and
/// answering 503; <see cref="Health.Ready"/> once it serves. Null from a server older than the field.
/// </param>
public sealed record HealthReport(
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("auth")] BearerAuthSnapshot? Auth = null,
    [property: JsonPropertyName("state")] string? State = null);

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

    /// <summary>The /health state of a server that is bound but still reading its bearer token.</summary>
    public const string Starting = "starting";

    /// <summary>The /health state of a server that serves.</summary>
    public const string Ready = "ready";

    /// <summary>How long the start-up probe waits for an answer.</summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMilliseconds(500);

    public enum PortState
    {
        /// <summary>Nothing recognisable there. Bind it, and let the bind decide.</summary>
        Free,

        /// <summary>
        /// A razorgraph-mcp owns the port: serving, or bound and still starting. A second start is
        /// redundant either way, so the two are one state. Nothing to do.
        /// </summary>
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
    ///
    /// The body is read whatever the status. A razorgraph-mcp still reading its token answers 503
    /// with its normal body, and that is a razorgraph-mcp all the same.
    /// </remarks>
    public static Task<(PortState State, string? Version)> ProbeAsync(int port) => ProbeAsync(port, ProbeTimeout);

    /// <summary>
    /// <see cref="ProbeAsync(int)"/> with the wait chosen by the caller. The tests pass a long one,
    /// because 500 ms is a start-up budget, and a loaded test host can miss it against a server
    /// that is there.
    /// </summary>
    internal static async Task<(PortState State, string? Version)> ProbeAsync(int port, TimeSpan timeout)
    {
        using HttpClient client = new() { Timeout = timeout };

        HealthReport? report;
        try
        {
            using HttpResponseMessage response = await client.GetAsync($"http://127.0.0.1:{port}/health");
            report = await response.Content.ReadFromJsonAsync<HealthReport>();
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
