namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// End-to-end over a console app written as top-level statements.
///
/// A separate fixture from SampleApp because only a console app can fail these:
/// the web SDK emits a `public partial class Program` for
/// WebApplicationFactory&lt;Program&gt;, which hands the extractor's type walk a
/// TypeDeclarationSyntax it would have found regardless. Every web fixture in
/// this repo therefore gets a Program node by accident, which is exactly why the
/// gap these tests cover survived unnoticed — the graph reported two classes and
/// seven methods for a viewer whose entire startup, and every call it made, was
/// invisible.
/// </summary>
[Trait("Category", "Integration")]
public class TopLevelStatementsIntegrationTests : IAsyncLifetime
{
    private const string EntryPointId = "m:Program.<Main>$(string[])";
    private const string Nav = "TopLevelApp.Nav";

    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _graph;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_graph != null) return;

            var fixtureDir = Path.Combine(RepoRoot(), "tests", "fixtures", "TopLevelApp");
            var projectPath = Path.Combine(fixtureDir, "TopLevelApp.csproj");
            Assert.True(File.Exists(projectPath), $"Fixture project missing: {projectPath}");

            EnsureRestored(fixtureDir);

            await using var builder = new GraphBuilder();
            _graph = await builder.BuildFromProjectAsync(projectPath);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", "restore")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of fixture failed:\n{output}");
    }

    private static string MethodId(string name) =>
        _graph!.Nodes.Single(n => n.Type == NodeType.Method && n.Name == name
            && n.GetProperty<string>("declaringType") == Nav).Id;

    private static IEnumerable<string> CallersOf(string methodId) =>
        _graph!.Incoming(methodId).Where(e => e.Type == EdgeType.Calls).Select(e => e.FromId);

    [Fact]
    public void Builds_ClassAndMethodNodes_ForTheSynthesizedEntryPoint()
    {
        // The compiler's Program class and <Main>$ exist only as symbols; nothing
        // in the syntax tree declares either.
        var program = _graph!.Nodes.Single(n => n.Type == NodeType.Class && n.Name == "Program");
        Assert.EndsWith("Program.cs", program.FilePath!, StringComparison.OrdinalIgnoreCase);

        var entryPoint = _graph.GetNode(EntryPointId);
        Assert.NotNull(entryPoint);
        Assert.Equal(NodeType.Method, entryPoint!.Type);

        // And it hangs off its class like any other method.
        Assert.Contains(_graph.Outgoing(program.Id),
            e => e.Type == EdgeType.Contains && e.ToId == EntryPointId);
    }

    [Fact]
    public void Marks_TheSynthesizedEntryPoint_AsMain()
    {
        // Without this the method the whole application starts from carries no
        // entry-point kind, so escape analysis has no root to report against.
        Assert.Equal("main", _graph!.GetNode(EntryPointId)!.GetProperty<string>("entryPointKind"));
    }

    [Fact]
    public void Builds_CallEdges_OutOfTopLevelStatements()
    {
        Assert.Contains(EntryPointId, CallersOf(MethodId("FindPath")));
        Assert.Contains(EntryPointId, CallersOf(MethodId("Accumulate")));
        Assert.Contains(EntryPointId, CallersOf(MethodId("Risky")));
    }

    [Fact]
    public void LeavesGenuinelyUnreachedMethods_WithoutCallers()
    {
        // The counterweight: the fix must make Main's callees reachable without
        // making everything reachable. NeverCalled is called from nowhere and
        // must still say so, or "who calls this" stops meaning anything.
        Assert.Empty(CallersOf(MethodId("NeverCalled")));
    }

    [Fact]
    public void Attributes_MemberAccess_FromTopLevelStatements()
    {
        var budget = _graph!.Nodes.Single(n => n.Type == NodeType.Property && n.Name == "Budget");

        var access = _graph.Incoming(budget.Id)
            .Where(e => e.Type is EdgeType.Reads or EdgeType.Writes)
            .Select(e => e.FromId)
            .ToList();

        Assert.Contains(EntryPointId, access);
    }

    [Fact]
    public void Attributes_Throws_FromTopLevelStatements()
    {
        var throws = _graph!.GetNode(EntryPointId)!.GetProperty<List<string>>("throws") ?? new();

        Assert.Contains("System.ArgumentOutOfRangeException", throws);
    }

    [Fact]
    public void Records_CatchGuards_WrittenInTopLevelStatements()
    {
        // The try sits in a global statement, not in a method body. If the guard
        // were missed, Risky's InvalidOperationException would be reported as
        // escaping the program when the code plainly handles it.
        var call = _graph!.Outgoing(EntryPointId)
            .Single(e => e.Type == EdgeType.Calls && e.ToId == MethodId("Risky"));

        Assert.Contains("System.InvalidOperationException", call.GetProperty<List<string>>("guardedBy") ?? new());

        var escapes = _graph.Outgoing(EntryPointId).Where(e => e.Type == EdgeType.Escapes).ToList();
        Assert.DoesNotContain(escapes,
            e => e.GetProperty<string>("exceptionType") == "System.InvalidOperationException");
    }
}
