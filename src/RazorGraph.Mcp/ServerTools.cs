namespace RazorGraph.Mcp;

using System.ComponentModel;
using System.Diagnostics;
using ModelContextProtocol.Server;

/// <summary>
/// What this server is, and which build of it you are actually talking to.
/// </summary>
/// <remarks>
/// Exists because of the hot-reload loop. Update-McpServer.ps1 publishes to a timestamped
/// directory and repoints a 'current' junction, so client config never changes -- which also
/// means the path a session dials says nothing about which build answers it. After a swap the
/// only way to tell was to inspect the process from outside, and a claim about which code is
/// running should not require leaving the tools to verify.
/// </remarks>
[McpServerToolType]
public sealed class ServerTools(GraphStore store)
{
    [McpServerTool(Name = "server_info")]
    [Description(
        "Which build of razorgraph-mcp is answering right now: version, the resolved build " +
        "directory behind the 'current' junction, process id, uptime, and how many graphs are " +
        "loaded. Cheap. Use it after a rebuild to confirm the swap landed, and to tell a stale " +
        "server from a stale graph when a query answers with something you did not expect.")]
    public string ServerInfo()
    {
        var process = Process.GetCurrentProcess();

        return ToolResponses.ToJson(new
        {
            server = "razorgraph-mcp",
            version = typeof(ServerTools).Assembly.GetName().Version?.ToString() ?? "0.0.0",

            // Resolved, not as-launched. A process started through the junction reports the LINK
            // as its path, so the as-launched value is the one thing that is identical across
            // every build and therefore useless for telling them apart.
            build = ResolvedBuildDirectory(),

            processId = Environment.ProcessId,
            uptimeSeconds = (int)(DateTime.Now - process.StartTime).TotalSeconds,
            graphsLoaded = store.List().Count,
        });
    }

    /// <summary>The real directory this build lives in, following 'current' if it is a junction.</summary>
    /// <remarks>
    /// ResolveLinkTarget returns null for an ordinary directory, which is the answer when the
    /// server was started directly out of a build output rather than through the rotation -- so
    /// falling back to the base directory is correct rather than a failure case.
    /// </remarks>
    private static string ResolvedBuildDirectory()
    {
        var baseDirectory = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);

        try
        {
            return new DirectoryInfo(baseDirectory).ResolveLinkTarget(returnFinalTarget: true)?.FullName
                ?? baseDirectory;
        }
        catch (IOException)
        {
            return baseDirectory;
        }
    }
}
