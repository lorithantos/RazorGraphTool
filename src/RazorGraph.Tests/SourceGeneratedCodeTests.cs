namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// Source-generator output, over the SourceGenerated fixture: CommunityToolkit.Mvvm's
/// [ObservableProperty], in its field form (the whole property generated) and its
/// partial-property form (declaration authored, accessor bodies generated). Each
/// generated setter calls a partial On...Changed hook implemented in user code, and
/// the hooks are the only way to reach Record.
///
/// These pin that the extractor walks generated trees, which it does because every
/// scan enumerates Compilation.SyntaxTrees -- generator output included, once the
/// workspace has run the generators -- rather than Project.Documents, which holds
/// only what is on disk. Until 2026-11-24 the README and the empty-coverage caveat
/// said this was "not confirmed".
/// </summary>
[Trait("Category", "Integration")]
public class SourceGeneratedCodeTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _graph;

    private const string Thermostat = "ObservableLib.Thermostat";
    private const string TargetId = "prop:" + Thermostat + ".Target";
    private const string ModeId = "prop:" + Thermostat + ".Mode";
    private const string TargetFieldId = "field:" + Thermostat + "._target";
    private const string OnTargetChangedId = "m:" + Thermostat + ".OnTargetChanged(int)";
    private const string OnModeChangedId = "m:" + Thermostat + ".OnModeChanged(string?)";
    private const string RecordId = "m:" + Thermostat + ".Record(string)";

    private static string FixtureDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        var root = dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
        return Path.Combine(root, "tests", "fixtures", "SourceGenerated");
    }

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_graph != null) return;

            var fixtureDir = FixtureDir();
            var solutionPath = Path.Combine(fixtureDir, "SourceGenerated.sln");
            Assert.True(File.Exists(solutionPath), $"Fixture solution missing: {solutionPath}");

            EnsureRestored(fixtureDir);

            await using var builder = new GraphBuilder();
            _graph = await builder.BuildFromSolutionAllAsync(solutionPath);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>
    /// Restore, not build: the generator ships in the package, and the workspace runs
    /// it from there. Without the restore there is no generator and nothing to test.
    /// </summary>
    private static void EnsureRestored(string fixtureDir)
    {
        if (File.Exists(Path.Combine(fixtureDir, "ObservableLib", "obj", "project.assets.json"))
            && File.Exists(Path.Combine(fixtureDir, "ObservableLib.Tests", "obj", "project.assets.json"))) return;

        var psi = new ProcessStartInfo("dotnet", "restore SourceGenerated.sln")
        {
            WorkingDirectory = fixtureDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        using var process = Process.Start(psi)!;
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of the SourceGenerated fixture failed:\n{output}");
    }

    private static GraphNode Node(string id) =>
        _graph!.GetNode(id) ?? throw new Xunit.Sdk.XunitException($"No node {id} in the graph.");

    private static bool HasEdge(string fromId, EdgeType type, string toId) =>
        _graph!.Outgoing(fromId).Any(e => e.Type == type && e.ToId == toId);

    [Fact]
    public void AWhollyGeneratedProperty_IsANode_MarkedGenerated()
    {
        // Nothing in Thermostat.cs declares Target; only the generator's tree does.
        var target = Node(TargetId);

        Assert.Equal(NodeType.Property, target.Type);
        Assert.EndsWith(".g.cs", target.FilePath);
        Assert.True(target.GetProperty<bool>("generated"));
    }

    [Fact]
    public void AGeneratedSetter_CallsTheHookUserCodeImplements()
    {
        Assert.True(HasEdge(TargetId, EdgeType.Calls, OnTargetChangedId),
            "the field-form setter's call to OnTargetChanged was not walked");
        Assert.True(HasEdge(ModeId, EdgeType.Calls, OnModeChangedId),
            "the partial-property setter's call to OnModeChanged was not walked");
    }

    [Fact]
    public void AGeneratedAccessor_ReadsAndWritesItsBackingField()
    {
        Assert.True(HasEdge(TargetId, EdgeType.Reads, TargetFieldId));
        Assert.True(HasEdge(TargetId, EdgeType.Writes, TargetFieldId));
    }

    [Fact]
    public void ATestSettingAGeneratedProperty_CoversWhatItsSetterReaches()
    {
        // test -Writes-> property -Calls-> On...Changed -Calls-> Record: three steps.
        var covering = new GraphQuery(_graph!)
            .GetCoveringTests(RecordId)
            .ToDictionary(t => t.Test.Name, t => t.Depth);

        Assert.Equal(3, covering["SettingTarget_IsLogged"]);
        Assert.Equal(3, covering["SettingMode_IsLogged"]);
    }
}
