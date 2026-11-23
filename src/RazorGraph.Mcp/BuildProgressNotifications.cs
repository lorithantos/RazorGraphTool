namespace RazorGraph.Mcp;

using ModelContextProtocol;
using RazorGraph.Extractor;

/// <summary>
/// Turns a build's progress into MCP progress notifications. The SDK binds a
/// tool parameter of type IProgress&lt;ProgressNotificationValue&gt; to one that
/// forwards to the client when the request carried a progressToken, and ignores
/// reports when it did not -- so the build reports either way and the client
/// decides whether anyone hears it.
/// </summary>
internal static class BuildProgressNotifications
{
    public static IProgress<BuildProgress>? Forward(IProgress<ProgressNotificationValue>? client) =>
        client is null ? null : new Forwarder(client);

    /// <summary>
    /// The notification for one build step. Progress counts completed phases, with
    /// a fraction for the project reached inside a per-project phase; Total is the
    /// phase count. The protocol wants progress to increase with every
    /// notification, so project n of m sits at n/(m+1) of the way through its phase:
    /// strictly after the phase's own start, strictly before the next phase.
    /// </summary>
    public static ProgressNotificationValue ToNotification(BuildProgress step)
    {
        var within = step.ProjectCount > 0 ? step.Project / (float)(step.ProjectCount + 1) : 0f;
        return new ProgressNotificationValue
        {
            Progress = step.PhaseNumber - 1 + within,
            Total = step.PhaseCount,
            Message = step.ToString()
        };
    }

    private sealed class Forwarder(IProgress<ProgressNotificationValue> client) : IProgress<BuildProgress>
    {
        public void Report(BuildProgress value) => client.Report(ToNotification(value));
    }
}
