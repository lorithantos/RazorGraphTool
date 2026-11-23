namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Query;
using Xunit;

/// <summary>
/// The bound on git ls-files is real: a lister that never exits is killed at the timeout and
/// answered with null, the same "could not answer" a git failure gets, so List falls back to
/// a directory walk and says so in ignoreSource instead of hanging its caller.
/// </summary>
/// <remarks>
/// The stand-in is ping, which prints a little and then sits for thirty seconds: a lister
/// whose stdout stays open until it exits, exactly the shape that defeated a ReadToEnd placed
/// before the wait. The test bounds itself, so a regression fails here rather than stalling
/// the suite.
/// </remarks>
public class SourceFilesListerTimeoutTests
{
    [Fact]
    public async Task ListerThatNeverExits_IsKilledAtTheTimeout_AndAnswersNull()
    {
        var pid = 0;
        var clock = Stopwatch.StartNew();
        var listing = Task.Run(() => SourceFiles.ListFiles(
            "ping", ["-n", "30", "127.0.0.1"], Path.GetTempPath(), TimeSpan.FromSeconds(1), id => pid = id));

        var first = await Task.WhenAny(listing, Task.Delay(TimeSpan.FromSeconds(15)));

        Assert.True(first == listing, "ListFiles was still waiting on the stand-in after 15 s: the timeout never fired.");
        Assert.Null(await listing);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:0.0} s against a 1 s timeout");
        Assert.NotEqual(0, pid);
        Assert.False(StillRunning(pid, "PING"), $"the stand-in (pid {pid}) outlived the timeout");
    }

    private static bool StillRunning(int pid, string name)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited && string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
