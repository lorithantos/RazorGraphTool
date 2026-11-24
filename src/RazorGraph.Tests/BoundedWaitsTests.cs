namespace RazorGraph.Tests;

using System.Diagnostics;
using Xunit;

/// <summary>
/// The two ways a setup's wait on a process never ended, each made to end. The
/// first is the shape the restore helpers had: the process exits, but a process it
/// started inherited its stdout and keeps it open, so reading to the end of stdout
/// waits on the grandchild rather than on the process. The second is a process
/// that does not exit at all. Windows only: the processes are cmd and ping.
/// </summary>
public sealed class BoundedWaitsTests
{
    private static readonly TimeSpan Short = TimeSpan.FromSeconds(1);

    [Fact]
    public void RunProcess_WhenAGrandchildHoldsTheOutputOpen_FailsFast_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;

        // cmd exits at once; the ping it starts in the background inherits cmd's
        // stdout, our pipe, and holds it for about five seconds.
        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<TimeoutException>(() => BoundedWaits.RunProcess(
            "cmd.exe", "/c start \"\" /b ping -n 6 127.0.0.1", Path.GetTempPath(), TimeSpan.FromSeconds(30), Short));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4), $"took {clock.Elapsed.TotalSeconds:0.#} s");
        Assert.Contains("exited with 0", ex.Message);
        Assert.Contains("a process it started holds the pipe", ex.Message);
    }

    [Fact]
    public void RunProcess_WhenTheProcessDoesNotExit_KillsIt_AndSaysSo()
    {
        if (!OperatingSystem.IsWindows()) return;

        var clock = Stopwatch.StartNew();
        var ex = Assert.Throws<TimeoutException>(() => BoundedWaits.RunProcess(
            "ping", "-n 30 127.0.0.1", Path.GetTempPath(), Short, Short));

        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed.TotalSeconds:0.#} s");
        Assert.Contains("did not finish within 1 s", ex.Message);
        Assert.Contains("127.0.0.1", ex.Message);
    }

    [Fact]
    public void RunProcess_ReturnsTheExitCode_AndBothStreams()
    {
        if (!OperatingSystem.IsWindows()) return;

        var (exitCode, output) = BoundedWaits.RunProcess(
            "cmd.exe", "/c echo out& echo err 1>&2& exit 3", Path.GetTempPath(), TimeSpan.FromSeconds(30), Short);

        Assert.Equal(3, exitCode);
        Assert.Contains("out", output);
        Assert.Contains("err", output);
    }

    [Fact]
    public async Task WithinAsync_WhenTheWorkOutlivesItsBound_FailsNamingIt()
    {
        var ex = await Assert.ThrowsAsync<TimeoutException>(() => BoundedWaits.WithinAsync(
            async ct => { await Task.Delay(Timeout.Infinite, ct); return 0; }, TimeSpan.FromMilliseconds(200), "the stuck build"));

        Assert.Contains("the stuck build did not finish within 0.2 s", ex.Message);
    }
}
