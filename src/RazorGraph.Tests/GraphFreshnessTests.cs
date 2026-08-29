namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using Xunit;

/// <summary>
/// Per-node staleness. The repo-level signal saturates — one edit into a session
/// it is true and never returns — so what a consumer needs is which of THESE
/// answers to doubt, not whether anything at all has moved.
/// </summary>
public class GraphFreshnessTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-fresh-{Guid.NewGuid():N}")).FullName;

    private string WriteFile(string name, DateTime writtenUtc)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, "// content");
        File.SetLastWriteTimeUtc(path, writtenUtc);
        return path;
    }

    private static CodeGraph GraphBuiltAt(DateTimeOffset? builtAt, params (string Id, string? Path)[] nodes)
    {
        var graph = new CodeGraph { BuiltAt = builtAt };

        foreach (var (id, path) in nodes)
        {
            graph.AddNode(new GraphNode
            {
                Id = id,
                Type = NodeType.Class,
                Name = id,
                FilePath = path,
            });
        }

        return graph;
    }

    [Fact]
    public void AFileWrittenAfterTheBuild_IsFlagged()
    {
        var builtAt = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        string edited = WriteFile("Edited.cs", builtAt.UtcDateTime.AddMinutes(5));

        var graph = GraphBuiltAt(builtAt, ("type:Edited", edited));

        Assert.True(new GraphFreshness(graph).WrittenSinceBuild(graph.GetNode("type:Edited")));
    }

    /// <summary>
    /// The case that makes this worth having: two files, one edited. A
    /// repository-level flag calls both suspect and so distinguishes nothing.
    /// </summary>
    [Fact]
    public void AnUntouchedFileIsNotFlagged_EvenWhenASiblingWasEdited()
    {
        var builtAt = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        string edited = WriteFile("Edited.cs", builtAt.UtcDateTime.AddMinutes(5));
        string untouched = WriteFile("Untouched.cs", builtAt.UtcDateTime.AddMinutes(-30));

        var graph = GraphBuiltAt(builtAt, ("type:Edited", edited), ("type:Untouched", untouched));
        var freshness = new GraphFreshness(graph);

        Assert.True(freshness.WrittenSinceBuild(graph.GetNode("type:Edited")));
        Assert.False(freshness.WrittenSinceBuild(graph.GetNode("type:Untouched")));
    }

    [Fact]
    public void ADeletedFileIsFlagged()
    {
        var builtAt = new DateTimeOffset(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
        string gone = Path.Combine(_dir, "NeverExisted.cs");

        var graph = GraphBuiltAt(builtAt, ("type:Gone", gone));

        Assert.True(new GraphFreshness(graph).WrittenSinceBuild(graph.GetNode("type:Gone")));
    }

    [Fact]
    public void WithoutABuildStamp_TheAnswerIsUnknownRatherThanFresh()
    {
        string untouched = WriteFile("Untouched.cs", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        var graph = GraphBuiltAt(builtAt: null, ("type:Untouched", untouched));

        Assert.Null(new GraphFreshness(graph).WrittenSinceBuild(graph.GetNode("type:Untouched")));
    }

    [Fact]
    public void ANodeWithNoFileIsUnknown()
    {
        var graph = GraphBuiltAt(DateTimeOffset.UtcNow, ("type:Synthetic", null));

        Assert.Null(new GraphFreshness(graph).WrittenSinceBuild(graph.GetNode("type:Synthetic")));
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
