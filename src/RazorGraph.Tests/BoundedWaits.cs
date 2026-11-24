namespace RazorGraph.Tests;

using System.Diagnostics;
using System.Text;
using Xunit;

/// <summary>
/// Bounds for the waits a test's setup makes on work outside the test host: a
/// <c>dotnet restore</c> it starts, and a build that loads through MSBuild. A full
/// run once sat for eight minutes in the setup of BuildFailureReportingTests and
/// UnrestoredProjectTests with nothing reported, because their restore read stdout
/// to the end before any bound -- and the end of a pipe comes only when every
/// process holding it has let go, which a grandchild can put off indefinitely.
/// Each wait here ends in a failure that says which wait it was and what the
/// process had written, instead of a host that never finishes.
/// </summary>
internal static class BoundedWaits
{
    /// <summary>A restore of a few small projects takes seconds; this is a ceiling, not a budget.</summary>
    internal static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(3);

    /// <summary>A build of a fixture takes seconds; this is a ceiling, not a budget.</summary>
    internal static readonly TimeSpan BuildTimeout = TimeSpan.FromMinutes(3);

    /// <summary>
    /// How long output may stay open after the process exits. Only a descendant that
    /// inherited the pipe can hold it that long.
    /// </summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>One restore at a time across the whole test run; see <see cref="Restore"/>.</summary>
    private static readonly object RestoreGate = new();

    /// <summary>
    /// Runs <c>dotnet restore</c> on <paramref name="target"/> in
    /// <paramref name="workingDirectory"/> and fails with its output unless it exits 0.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>-nodeReuse:false</c> is the fix for the hang this class was written for,
    /// confirmed 2026-11-24 from a dump of the hung testhost and its handle table:
    /// three threads sat in ReadToEnd on a restore's stdout, the restores had exited,
    /// and three orphaned MSBuild nodes started with /nodeReuse:true each held that
    /// restore's stdout and stderr -- pipes the testhost had created. A reusable node
    /// is meant to outlive the build that started it, and it inherits that build's
    /// standard handles, so the pipe never closed. Without reuse the nodes exit with
    /// the restore.
    /// </para>
    /// <para>
    /// The gate serializes restores across test classes. Several classes restore the
    /// same MultiProject fixture, and in a fresh checkout they all found it unrestored
    /// at once and raced on the same obj files.
    /// </para>
    /// </remarks>
    internal static void Restore(string workingDirectory, string target, string description)
    {
        lock (RestoreGate)
        {
            var (exitCode, output) = RunProcess(
                "dotnet", $"restore {target} -nodeReuse:false", workingDirectory, RestoreTimeout, DrainTimeout);
            Assert.True(exitCode == 0, $"dotnet restore of {description} failed:\n{output}");
        }
    }

    /// <summary>
    /// Starts a process with its own stdin, already closed, and both output streams
    /// read as they arrive, so neither a prompt nor a full pipe can stall it. Throws
    /// <see cref="TimeoutException"/> when it runs past <paramref name="timeout"/>,
    /// after killing it and every process it started, or when it exits but its output
    /// stays open past <paramref name="drain"/> because something it started still
    /// holds the pipe.
    /// </summary>
    internal static (int ExitCode, string Output) RunProcess(
        string fileName, string arguments, string workingDirectory, TimeSpan timeout, TimeSpan drain)
    {
        var psi = new ProcessStartInfo(fileName, arguments)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };

        var output = new StringBuilder();
        var stdoutClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stderrClosed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var process = Process.Start(psi)!;
        process.OutputDataReceived += (_, e) => Append(e.Data, stdoutClosed);
        process.ErrorDataReceived += (_, e) => Append(e.Data, stderrClosed);
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var command = $"'{fileName} {arguments}' in {workingDirectory}";
        if (!process.WaitForExit(timeout))
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            process.WaitForExit(drain);
            throw new TimeoutException(
                $"{command} did not finish within {timeout.TotalSeconds:0.###} s; it and its process tree were killed. " +
                $"Output so far:\n{Snapshot()}");
        }

        if (!Task.WhenAll(stdoutClosed.Task, stderrClosed.Task).Wait(drain))
        {
            throw new TimeoutException(
                $"{command} exited with {process.ExitCode}, but its output was still open {drain.TotalSeconds:0.###} s later: " +
                $"a process it started holds the pipe. Output so far:\n{Snapshot()}");
        }

        return (process.ExitCode, Snapshot());

        void Append(string? line, TaskCompletionSource closed)
        {
            if (line is null) { closed.TrySetResult(); return; }
            lock (output) output.AppendLine(line);
        }

        string Snapshot()
        {
            lock (output) return output.ToString();
        }
    }

    /// <summary>
    /// Awaits work that takes a cancellation token, cancelling it at
    /// <paramref name="timeout"/> and abandoning it a little after, should it not
    /// honour the cancellation. Either way the failure names <paramref name="what"/>.
    /// </summary>
    internal static async Task<T> WithinAsync<T>(Func<CancellationToken, Task<T>> start, TimeSpan timeout, string what)
    {
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            return await start(cts.Token).WaitAsync(timeout + DrainTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException || (ex is OperationCanceledException && cts.IsCancellationRequested))
        {
            throw new TimeoutException($"{what} did not finish within {timeout.TotalSeconds:0.###} s.", ex);
        }
    }
}
