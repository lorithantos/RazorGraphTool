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
internal sealed class ServerTools(GraphStore store)
{
    [McpServerTool(Name = "server_info")]
    [Description(
        "Which build of razorgraph-mcp is answering right now: version, 'build' (the directory " +
        "this process is running from, resolved through the 'current' junction once at startup), " +
        "'currentNow' (present only when the junction has since moved to another build, as it " +
        "does mid-rotation), process id, uptime, and how many graphs are " +
        "loaded, and what normalising its environment at startup did: 'environment' says whether " +
        "it was applied (or why it was skipped) and which variables the launching shell had that " +
        "were dropped, changed, or kept as RAZORGRAPH_* configuration. Variable NAMES only, never " +
        "values. Cheap. Use it after a rebuild to confirm the swap landed, and to tell a stale " +
        "server from a stale graph when a query answers with something you did not expect.")]
    public string ServerInfo() => ServerInfo(ServerEnvironment.Last);

    /// <summary>server_info with the environment report passed in, so a test can supply one.</summary>
    /// <remarks>
    /// The real report comes from ServerEnvironment.Apply, which only the entry point may call: it
    /// rewrites the process environment, and a test process shares one across every test.
    /// </remarks>
    internal string ServerInfo(EnvironmentReport? environment) => ServerInfo(environment, BuildLocation.Running);

    /// <summary>server_info with the build location passed in too, so a test can move a junction under one.</summary>
    internal string ServerInfo(EnvironmentReport? environment, BuildLocation location)
    {
        var process = Process.GetCurrentProcess();

        // Null means Apply never ran in this process -- a host that is not Program, such as a
        // test -- which is a different answer from "skipped", so it says so rather than going quiet.
        var report = environment ?? new EnvironmentReport(
            false, "not run: this process did not start through razorgraph-mcp's entry point", [], [], []);

        return ToolResponses.ToJson(new
        {
            server = "razorgraph-mcp",
            version = typeof(ServerTools).Assembly.GetName().Version?.ToString() ?? "0.0.0",

            // Resolved, not as-launched. A process started through the junction reports the LINK
            // as its path, so the as-launched value is the one thing that is identical across
            // every build and therefore useless for telling them apart. Resolved at startup, not
            // now: mid-rotation the junction already names the next build while this process
            // still answers, and currentNow (absent otherwise) is where that reading goes.
            build = location.Build,
            currentNow = location.CurrentNow(),

            processId = Environment.ProcessId,
            uptimeSeconds = (int)(DateTime.Now - process.StartTime).TotalSeconds,
            graphsLoaded = store.List().Count,

            // Names only: a launcher's environment can hold tokens and connection strings, and
            // this answer lands in transcripts. skippedBecause is absent when it was applied.
            environment = new
            {
                applied = report.Applied,
                skippedBecause = report.SkippedBecause,
                dropped = report.Dropped,
                changed = report.Changed,
                keptFromLauncher = report.KeptFromLauncher,
            },
        });
    }
}
