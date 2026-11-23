namespace RazorGraph.Tests;

using System.Diagnostics;
using System.Text.Json;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Serialization;
using Xunit;

/// <summary>
/// Every git RazorGraph starts gets a stdin of its own. Under the stdio MCP transport the
/// server's stdin is the client's pipe, with a synchronous read pending on it between requests,
/// and git for Windows blocks on that inherited handle at startup until the client closes the
/// pipe, which a client never does mid-session. <c>build_solution</c> over stdio hung on
/// <c>git ls-files</c> that way with no bound, and <c>save_graph</c> waited out its 3 s
/// <c>check-ignore</c> and then advised ignoring a path git already ignores.
/// </summary>
/// <remarks>
/// The real server, driven the way a client drives it: this process holds the server's stdin
/// open and writes nothing while the server works, which is the whole condition. The test host's
/// own stdin cannot honestly be made that pipe: SetStdHandle would swap it for every test running
/// in parallel, and those that start <c>dotnet restore</c> would inherit it too. Each answer is
/// bounded, so a regression fails here rather than hanging the suite. Off Windows the hang does
/// not occur and these pass regardless.
/// </remarks>
public sealed class StdioChildStdinTests : IDisposable
{
    /// <summary>Process start and JIT, before the server has answered anything.</summary>
    private static readonly TimeSpan StartWithin = TimeSpan.FromSeconds(20);

    /// <summary>
    /// A request that starts git. Fixed, these answer in well under a second; the bound only
    /// turns an unbounded hang into a failure, and save_graph's regression shows as its advice.
    /// </summary>
    private static readonly TimeSpan AnswerWithin = TimeSpan.FromSeconds(5);

    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-stdin-{Guid.NewGuid():N}")).FullName;

    public void Dispose()
    {
        try
        {
            // git writes its object files read-only, and Delete refuses those.
            foreach (var file in Directory.EnumerateFiles(_dir, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>The grep disk section lists files through <c>git ls-files</c>.</summary>
    [Fact]
    public void GrepOverStdio_ListsFilesThroughGit_WhileTheClientHoldsStdinOpen()
    {
        RunGit("init", "-q");
        File.WriteAllText(Path.Combine(_dir, "notes.md"), "needle\n");
        using var server = StdioServer.Start();

        var disk = server.Tool("grep", new { pattern = "needle", folder = _dir }).GetProperty("disk");

        Assert.StartsWith("git ls-files", disk.GetProperty("ignoreSource").GetString(), StringComparison.Ordinal);
        Assert.Equal(1, disk.GetProperty("returned").GetInt32());
    }

    /// <summary>
    /// <c>save_graph</c> asks <c>git check-ignore</c> about the path it wrote. A git that cannot
    /// answer reads as "not ignored", so the advice appearing is the symptom.
    /// </summary>
    [Fact]
    public void SaveGraphOverStdio_HearsFromGitThatThePathIsIgnored()
    {
        RunGit("init", "-q");
        File.WriteAllText(Path.Combine(_dir, ".gitignore"), "graphs/\n");
        var input = Path.Combine(_dir, "input.json");
        File.WriteAllText(input, GraphSerializer.ToJson(new CodeGraph { BuiltAt = DateTimeOffset.UtcNow }));
        using var server = StdioServer.Start();
        server.Tool("load_graph", new { path = input, graphId = "g" });

        var saved = server.Tool("save_graph", new { outputPath = Path.Combine(_dir, "graphs", "g.json"), graphId = "g" });

        Assert.False(saved.TryGetProperty("gitAdvice", out var advice), $"git check-ignore did not answer: {advice}");
    }

    private void RunGit(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _dir, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    /// <summary>
    /// The server executable the project reference copies next to the tests, spoken to as an MCP
    /// client speaks to it: newline-delimited JSON-RPC on a stdin this side keeps open.
    /// </summary>
    private sealed class StdioServer : IDisposable
    {
        private readonly Process _process;
        private int _lastId;

        private StdioServer(Process process) => _process = process;

        public static StdioServer Start()
        {
            var info = new ProcessStartInfo(Path.Combine(
                AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "RazorGraph.Mcp.exe" : "RazorGraph.Mcp"))
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            var server = new StdioServer(Process.Start(info)!);

            // The server logs to stderr; a full pipe there would stall it for a reason of our own.
            server._process.ErrorDataReceived += (_, _) => { };
            server._process.BeginErrorReadLine();

            server.Request("initialize", new
            {
                protocolVersion = "2025-06-18",
                capabilities = new { },
                clientInfo = new { name = "rg-tests", version = "0" },
            }, StartWithin);
            server.Send(new { jsonrpc = "2.0", method = "notifications/initialized" });
            return server;
        }

        /// <summary>Calls a tool and returns its JSON answer, failing on a tool error.</summary>
        public JsonElement Tool(string name, object arguments)
        {
            var result = Request("tools/call", new { name, arguments }, AnswerWithin).GetProperty("result");
            var text = result.GetProperty("content")[0].GetProperty("text").GetString()!;
            Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean(), $"{name} failed: {text}");
            return JsonDocument.Parse(text).RootElement.Clone();
        }

        private JsonElement Request(string method, object parameters, TimeSpan within)
        {
            var id = ++_lastId;
            Send(new { jsonrpc = "2.0", id, method, @params = parameters });

            var clock = Stopwatch.StartNew();
            while (true)
            {
                var remaining = within - clock.Elapsed;
                var read = _process.StandardOutput.ReadLineAsync();
                if (remaining <= TimeSpan.Zero || !read.Wait(remaining))
                    Assert.Fail($"{method} got no answer within {within.TotalSeconds} s while stdin was held open.");

                var line = read.Result ?? throw new InvalidOperationException($"The server closed stdout during {method}.");
                using var message = JsonDocument.Parse(line);
                if (message.RootElement.TryGetProperty("id", out var answered) && answered.GetInt32() == id)
                    return message.RootElement.Clone();
            }
        }

        private void Send(object message)
        {
            _process.StandardInput.WriteLine(JsonSerializer.Serialize(message));
            _process.StandardInput.Flush();
        }

        public void Dispose()
        {
            try
            {
                _process.StandardInput.Close();
                if (!_process.WaitForExit(5_000)) _process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) { }
            finally
            {
                // A hung git is the server's child: killing the tree is what clears it.
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
                _process.Dispose();
            }
        }
    }
}
