namespace RazorGraph.Tests;

using System.Text.Json;
using RazorGraph.Core.Graph;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// A graph carried through save_graph and load_graph -- which is how a host keeps
/// its graphs across a server restart -- must come back naming what it was
/// extracted from, not the scratch file it passed through. A consumer finds a
/// repository's graph by asking whether its source lies under the repository,
/// so a carried graph that reports the file instead is a graph nobody can find.
/// </summary>
public class GraphCarryOverTests : IDisposable
{
    private const string Solution = @"C:\repos\App\App.slnx";

    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-carry-{Guid.NewGuid():N}")).FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public async Task SaveThenLoad_KeepsTheOriginalSourceAndBuiltAt_AndReportsTheFileAsLoadedFrom()
    {
        var builtAt = new DateTimeOffset(2026, 10, 12, 6, 6, 58, TimeSpan.Zero);
        var store = new GraphStore();
        store.Add(new CodeGraph { BuiltAt = builtAt }, Solution, requestedId: "app");
        var tools = new GraphStoreTools(store);
        var file = Path.Combine(_dir, "app.json");

        await tools.SaveGraph(file, "app");
        await tools.LoadGraph(file, "carried");

        var row = Row(tools, "carried");
        Assert.Equal(Solution, row.GetProperty("source").GetString());
        Assert.Equal(file, row.GetProperty("loadedFrom").GetString());
        Assert.Equal(builtAt, row.GetProperty("builtAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task Load_WithoutAnId_IsStillNamedAfterTheFile_NotTheSource()
    {
        // load_graph's id has always defaulted to the file name. A carried graph
        // now reports its solution as source, and must not change id on that
        // account -- the file is what the caller just named.
        var store = new GraphStore();
        store.Add(new CodeGraph(), Solution, requestedId: "app");
        var tools = new GraphStoreTools(store);
        var file = Path.Combine(_dir, "Rotated.json");
        await tools.SaveGraph(file, "app");

        using var doc = JsonDocument.Parse(await tools.LoadGraph(file));

        Assert.Equal("Rotated", doc.RootElement.GetProperty("graphId").GetString());
        Assert.Equal(Solution, doc.RootElement.GetProperty("source").GetString());
    }

    [Fact]
    public async Task Load_OfAFileSavedBeforeSourceWasRecorded_ReportsTheFileAsBoth()
    {
        // Nothing in the file says where the graph came from, so the file is the
        // only origin there is -- which is exactly what was reported before.
        var file = Path.Combine(_dir, "old.json");
        await File.WriteAllTextAsync(file, """{"formatVersion":"3.0","builtAt":"2026-10-01T00:00:00+00:00","nodes":[],"edges":[]}""");
        var tools = new GraphStoreTools(new GraphStore());

        await tools.LoadGraph(file, "old");

        var row = Row(tools, "old");
        Assert.Equal(file, row.GetProperty("source").GetString());
        Assert.Equal(file, row.GetProperty("loadedFrom").GetString());
    }

    [Fact]
    public async Task Resave_OfAGraphLoadedFromAnOldFile_CarriesThatFileOnAsItsSource()
    {
        // The old file is the only origin such a graph has, and a second carry
        // must not trade it for the second scratch file.
        var old = Path.Combine(_dir, "old.json");
        await File.WriteAllTextAsync(old, """{"formatVersion":"3.0","nodes":[],"edges":[]}""");
        var tools = new GraphStoreTools(new GraphStore());
        await tools.LoadGraph(old, "old");

        var again = Path.Combine(_dir, "again.json");
        await tools.SaveGraph(again, "old");
        await tools.LoadGraph(again, "again");

        var row = Row(tools, "again");
        Assert.Equal(old, row.GetProperty("source").GetString());
        Assert.Equal(again, row.GetProperty("loadedFrom").GetString());
    }

    [Fact]
    public void ListGraphs_OnABuiltGraph_HasNoLoadedFrom()
    {
        var store = new GraphStore();
        store.Add(new CodeGraph(), Solution, requestedId: "app");

        var row = Row(new GraphStoreTools(store), "app");

        Assert.Equal(Solution, row.GetProperty("source").GetString());
        Assert.False(row.TryGetProperty("loadedFrom", out _));
    }

    private static JsonElement Row(GraphStoreTools tools, string graphId)
    {
        using var doc = JsonDocument.Parse(tools.ListGraphs());
        return doc.RootElement.GetProperty("graphs").EnumerateArray()
            .Single(g => g.GetProperty("graphId").GetString() == graphId)
            .Clone();
    }
}
