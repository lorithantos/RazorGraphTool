namespace RazorGraph.Tests;

using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol;
using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// The grep tool in both tiers: the graph tier over hand-built graphs rooted at a
/// folder that need not exist, the disk tier over a temp folder, and the choice
/// between them through the MCP tool itself.
/// </summary>
public class TextSearchTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-grep-{Guid.NewGuid():N}")).FullName;

    private string WriteFile(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    public void Dispose()
    {
        GC.SuppressFinalize(this);
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

    // ---- graph tier -----------------------------------------------------------

    private const string Root = @"C:\a\b";

    /// <summary>
    /// A type and a method under C:\a\b, a same-named type under the sibling C:\a\bc
    /// that shares the prefix but not the folder, and one literal produced by the method.
    /// </summary>
    private static CodeGraph FolderGraph(string root = Root)
    {
        var graph = new CodeGraph { BuiltAt = DateTimeOffset.UtcNow };

        var widget = new GraphNode
        {
            Id = "type:App.Widget", Type = NodeType.Class, Name = "Widget",
            FilePath = Path.Combine(root, "Widget.cs"), LineStart = 7
        };
        widget.SetProperty("fullName", "App.Widget");
        graph.AddNode(widget);

        var spin = new GraphNode
        {
            Id = "m:App.Widget.Spin()", Type = NodeType.Method, Name = "Spin",
            FilePath = Path.Combine(root, "Widget.cs"), LineStart = 12
        };
        spin.SetProperty("declaringType", "App.Widget");
        graph.AddNode(spin);

        graph.AddNode(new GraphNode
        {
            Id = "type:Other.Widget", Type = NodeType.Class, Name = "Widget",
            FilePath = root + @"c\Other.cs", LineStart = 3
        });

        var literal = new GraphNode { Id = "lit:waves", Type = NodeType.Literal, Name = "waves.ini" };
        literal.SetProperty("value", "waves.ini");
        graph.AddNode(literal);

        var mention = new GraphEdge { FromId = "m:App.Widget.Spin()", ToId = "lit:waves", Type = EdgeType.Mentions };
        mention.Properties["line"] = 14;
        mention.Properties["provenance"] = "literal";
        graph.AddEdge(mention);

        return graph;
    }

    [Fact]
    public void Graph_MatchesANodeNameUnderTheFolder_WithItsRelativePathAndLine()
    {
        var result = TextSearch.SearchGraph(FolderGraph(), Root, TextSearch.Compile("^widget$", ignoreCase: true));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(("type:App.Widget", "name", "Widget.cs", (int?)7), (hit.NodeId, hit.MatchedOn, hit.Path, hit.Line));
    }

    [Fact]
    public void Graph_MatchesAMembersQualifiedName_WhenItsBareNameDoesNot()
    {
        var result = TextSearch.SearchGraph(FolderGraph(), Root, TextSearch.Compile(@"Widget\.Spin", ignoreCase: false));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(("m:App.Widget.Spin()", "qualifiedName"), (hit.NodeId, hit.MatchedOn));
    }

    [Fact]
    public void Graph_LocatesALiteralAtItsProducingMembersFileAndLine()
    {
        var result = TextSearch.SearchGraph(FolderGraph(), Root, TextSearch.Compile(@"waves\.ini", ignoreCase: true));

        var hit = Assert.Single(result.Hits);
        Assert.Equal(
            ("lit:waves", "Literal", "waves.ini", "literal", "Widget.cs", (int?)14, "m:App.Widget.Spin()"),
            (hit.NodeId, hit.NodeType, hit.Name, hit.MatchedOn, hit.Path, hit.Line, hit.FromId));
    }

    [Fact]
    public void Graph_ExcludesNodesOutsideTheFolder_IncludingASiblingThatSharesItsPrefix()
    {
        // C:\a\bc\Other.cs starts with "C:\a\b" as a string and is not under it.
        var result = TextSearch.SearchGraph(FolderGraph(), Root, TextSearch.Compile("Widget", ignoreCase: false));

        Assert.DoesNotContain(result.Hits, h => h.NodeId == "type:Other.Widget");
        Assert.Contains(result.Hits, h => h.NodeId == "type:App.Widget");
        Assert.False(TextSearch.IsUnder(@"C:\a\bc\Other.cs", Root));
        Assert.True(TextSearch.IsUnder(@"C:\a\b\Widget.cs", TextSearch.NormaliseFolder(@"C:\a\b\")));
    }

    // ---- disk tier ------------------------------------------------------------

    [Fact]
    public void Disk_ReportsTheLineNumberAndTheTrimmedLineCappedAt200()
    {
        var longTail = new string('x', 500);
        WriteFile(@"scripts\deploy.ps1", $"# header\n\n    $target = 'needle' {longTail}\nlast\n");

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("needle", ignoreCase: true), glob: null, limit: 50, offset: 0);

        var hit = Assert.Single(result.Hits);
        Assert.Equal(Path.Combine("scripts", "deploy.ps1"), hit.Path);
        Assert.Equal(3, hit.Line);
        Assert.Equal(TextSearch.TextCap, hit.Text.Length);
        Assert.StartsWith("$target = 'needle'", hit.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_SkipsABinaryFileAndCountsIt()
    {
        WriteFile("plain.txt", "needle\n");
        File.WriteAllBytes(Path.Combine(_dir, "blob.bin"), [.. "needle"u8, 0, .. "needle"u8]);

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("needle", ignoreCase: true), glob: null, limit: 50, offset: 0);

        Assert.Equal(["plain.txt"], result.Hits.Select(h => h.Path));
        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal(1, result.FilesScanned);
    }

    [Fact]
    public void Disk_GlobFiltersOnTheFileName()
    {
        WriteFile(@"a\one.json", "\"needle\": 1\n");
        WriteFile(@"a\two.md", "needle\n");

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("needle", ignoreCase: true), glob: "*.JSON", limit: 50, offset: 0);

        Assert.Equal([Path.Combine("a", "one.json")], result.Hits.Select(h => h.Path));
    }

    [Fact]
    public void Disk_PagesOverEveryMatchWithoutRepeatingOrSkipping_AndCountsThemAll()
    {
        WriteFile("a.txt", "needle 1\nhay\nneedle 2\n");
        WriteFile("b.txt", "needle 3\nneedle 4\nneedle 5\n");
        var regex = TextSearch.Compile("needle", ignoreCase: true);

        var walked = new List<TextSearch.DiskHit>();
        int? offset = 0;
        while (offset is { } from)
        {
            var page = TextSearch.SearchDisk(_dir, regex, glob: null, limit: 2, offset: from);
            Assert.Equal(5, page.TotalMatches);
            walked.AddRange(page.Hits);
            offset = page.NextOffset;
        }

        Assert.Equal(["needle 1", "needle 2", "needle 3", "needle 4", "needle 5"], walked.Select(h => h.Text));
        Assert.Equal(3, TextSearch.SearchDisk(_dir, regex, glob: null, limit: 2, offset: 1).NextOffset);
        Assert.Null(TextSearch.SearchDisk(_dir, regex, glob: null, limit: 2, offset: 3).NextOffset);
    }

    [Fact]
    public void Disk_OutsideGit_SkipsVendorAndBuildOutputDirectories()
    {
        WriteFile(@"src\app.js", "needle\n");
        WriteFile(@"node_modules\pkg\index.js", "needle\n");
        WriteFile(@"src\obj\gen.cs", "needle\n");

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("needle", ignoreCase: true), glob: null, limit: 50, offset: 0);

        Assert.Equal([Path.Combine("src", "app.js")], result.Hits.Select(h => h.Path));
        Assert.StartsWith("directory walk (not a git repository)", result.IgnoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_InAGitWorkingTree_HonoursGitignore()
    {
        RunGit("init", "-q");
        WriteFile(".gitignore", "secret/\n");
        WriteFile(@"kept\notes.md", "needle\n");
        WriteFile(@"secret\notes.md", "needle\n");

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("needle", ignoreCase: true), glob: null, limit: 50, offset: 0);

        Assert.Equal([Path.Combine("kept", "notes.md")], result.Hits.Select(h => h.Path));
        Assert.StartsWith("git ls-files", result.IgnoreSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Disk_NamesAFileThePatternTimedOutOn_AndKeepsGoing()
    {
        // Nested quantifiers against a near-miss: exponential backtracking, so the
        // one-second timeout is what ends it, not the match.
        WriteFile("evil.txt", new string('a', 40) + "!\n");
        WriteFile("fine.txt", "aaaa\n");

        var result = TextSearch.SearchDisk(_dir, TextSearch.Compile("^(a+)+$", ignoreCase: false), glob: null, limit: 50, offset: 0);

        Assert.Equal(["evil.txt"], result.TimedOutFiles);
        Assert.Equal(["fine.txt"], result.Hits.Select(h => h.Path));
    }

    private void RunGit(params string[] args)
    {
        var info = new ProcessStartInfo("git") { WorkingDirectory = _dir, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    // ---- the tool: a graph section and a disk section --------------------------

    private static GraphStore StoreOver(params (string Id, CodeGraph Graph)[] graphs)
    {
        var store = new GraphStore();
        foreach (var (id, graph) in graphs) store.Add(graph, source: "test://hand-built", requestedId: id);
        return store;
    }

    private static List<int> Lines(JsonElement section) =>
        section.GetProperty("hits").EnumerateArray().Select(h => h.GetProperty("line").GetInt32()).ToList();

    // FolderGraph puts Widget at line 7 and Spin, whose qualified name contains
    // Widget, at line 12 of Widget.cs. This file has "Widget" on lines 1, 7 and 12.
    private const string WidgetSource =
        "// Widget notes\n\n\n\n\n\nclass Widget {\n\n\n\n\n    void Spin() => Widget.Go();\n}\n";

    [Fact]
    public void Tool_GraphSection_CarriesTheGraphHitsAndItsStamps()
    {
        var store = StoreOver(("g", FolderGraph(_dir)));
        WriteFile("Widget.cs", "class Widget { }\n");

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("^Widget$", _dir));
        var graph = doc.RootElement.GetProperty("graph");

        Assert.Equal("g", graph.GetProperty("graphId").GetString());
        Assert.True(graph.TryGetProperty("builtAt", out _));
        var hit = Assert.Single(graph.GetProperty("hits").EnumerateArray().ToList());
        Assert.Equal("type:App.Widget", hit.GetProperty("nodeId").GetString());
        Assert.Equal("Widget.cs", hit.GetProperty("path").GetString());
        Assert.True(doc.RootElement.TryGetProperty("disk", out _));
    }

    [Fact]
    public void Tool_NoCoveringGraph_GivesAReasonAndEmptyGraphHits_AndAPopulatedDiskSection()
    {
        var store = StoreOver(("g", FolderGraph()));
        WriteFile("notes.md", "Widget\n");

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("Widget", _dir));
        var graph = doc.RootElement.GetProperty("graph");
        var disk = doc.RootElement.GetProperty("disk");

        Assert.Equal("no loaded graph covers folder", graph.GetProperty("reason").GetString());
        Assert.Equal(JsonValueKind.Array, graph.GetProperty("hits").ValueKind);
        Assert.Equal(0, graph.GetProperty("hits").GetArrayLength());
        Assert.False(graph.TryGetProperty("graphId", out _));
        Assert.Equal("notes.md", disk.GetProperty("hits")[0].GetProperty("path").GetString());
        Assert.Equal(1, disk.GetProperty("hits")[0].GetProperty("line").GetInt32());
    }

    [Fact]
    public void Tool_CoveringGraphWithNoMatch_StillFindsTheCodeTextOnDisk()
    {
        // A comment is code text the graph never indexed; only the disk can find it.
        var store = StoreOver(("g", FolderGraph(_dir)));
        WriteFile("Widget.cs", "// TODO: tighten the spin\nclass Widget { }\n");

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("TODO", _dir));

        Assert.Equal(0, doc.RootElement.GetProperty("graph").GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, doc.RootElement.GetProperty("disk").GetProperty("totalMatches").GetInt32());
    }

    [Fact]
    public void Tool_PrefersTheCurrentCoveringGraph_AndNamesTheOthers()
    {
        // "current" is added last, so it is the default, and it is the OLDER build:
        // newest-build-wins would pick "fresh", so only the current rule picks this one.
        var stale = FolderGraph(_dir);
        stale.BuiltAt = DateTimeOffset.UtcNow.AddDays(-1);
        var store = StoreOver(("fresh", FolderGraph(_dir)), ("current", stale));

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("Widget", _dir));
        var graph = doc.RootElement.GetProperty("graph");

        Assert.Equal("current", graph.GetProperty("graphId").GetString());
        Assert.Equal(["fresh"], graph.GetProperty("otherCoveringGraphs").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public void Tool_DiskSection_ExcludesALineTheGraphFound_AndKeepsOtherLinesOfThatFile()
    {
        var store = StoreOver(("g", FolderGraph(_dir)));
        WriteFile("Widget.cs", WidgetSource);

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("Widget", _dir));
        var disk = doc.RootElement.GetProperty("disk");

        Assert.Equal([7, 12], Lines(doc.RootElement.GetProperty("graph")));
        Assert.Equal([1], Lines(disk));
        Assert.Equal(1, disk.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, disk.GetProperty("alreadyInGraph").GetInt32());
    }

    [Fact]
    public void Tool_DiskSection_ExcludesEveryGraphHit_NotOnlyTheReturnedPage()
    {
        // limit 1 returns only the line-7 graph hit; line 12 belongs to the second
        // graph hit, which is on the NEXT graph page and must still be left out.
        var store = StoreOver(("g", FolderGraph(_dir)));
        WriteFile("Widget.cs", WidgetSource);

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("Widget", _dir, limit: 1));
        var disk = doc.RootElement.GetProperty("disk");

        Assert.Equal([7], Lines(doc.RootElement.GetProperty("graph")));
        Assert.Equal(1, disk.GetProperty("totalMatches").GetInt32());
        Assert.Equal(2, disk.GetProperty("alreadyInGraph").GetInt32());
    }

    [Fact]
    public void Tool_GlobFiltersGraphHits_ByFileName()
    {
        var graph = FolderGraph(_dir);
        graph.AddNode(new GraphNode
        {
            Id = "config:WidgetSettings", Type = NodeType.ConfigurationFile, Name = "WidgetSettings",
            FilePath = Path.Combine(_dir, "appsettings.json"), LineStart = 1
        });
        var store = StoreOver(("g", graph));

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("Widget", _dir, glob: "*.json"));
        var hits = doc.RootElement.GetProperty("graph").GetProperty("hits");

        Assert.Equal(["config:WidgetSettings"], hits.EnumerateArray().Select(h => h.GetProperty("nodeId").GetString()));
    }

    [Fact]
    public void Tool_PagesTheDiskSection_WithTruncatedAndNextOffset()
    {
        WriteFile("a.txt", "needle\nneedle\nneedle\n");
        var tools = new TextSearchTools(new GraphStore());

        using var first = JsonDocument.Parse(tools.Grep("needle", _dir, limit: 2));
        var disk = first.RootElement.GetProperty("disk");
        Assert.True(disk.GetProperty("truncated").GetBoolean());
        Assert.Equal(2, disk.GetProperty("nextOffset").GetInt32());
        Assert.Equal(3, disk.GetProperty("totalMatches").GetInt32());

        using var last = JsonDocument.Parse(tools.Grep("needle", _dir, limit: 2, diskOffset: 2));
        disk = last.RootElement.GetProperty("disk");
        Assert.False(disk.GetProperty("truncated").GetBoolean());
        Assert.False(disk.TryGetProperty("nextOffset", out _));
        Assert.Equal([3], Lines(disk));
    }

    [Fact]
    public void Tool_DiskOffset_PagesTheDiskSectionIndependentlyOfOffset()
    {
        // Graph hits at lines 7 and 12; disk hits, with those removed, on lines 1 and 2.
        var store = StoreOver(("g", FolderGraph(_dir)));
        WriteFile("Widget.cs", "// Widget one\n// Widget two\n\n\n\n\nclass Widget {\n\n\n\n\n    void Spin() => Widget.Go();\n}\n");
        var tools = new TextSearchTools(store);

        using var a = JsonDocument.Parse(tools.Grep("Widget", _dir, limit: 1, offset: 0, diskOffset: 1));
        Assert.Equal([7], Lines(a.RootElement.GetProperty("graph")));
        Assert.Equal([2], Lines(a.RootElement.GetProperty("disk")));

        using var b = JsonDocument.Parse(tools.Grep("Widget", _dir, limit: 1, offset: 1, diskOffset: 0));
        Assert.Equal([12], Lines(b.RootElement.GetProperty("graph")));
        Assert.Equal([1], Lines(b.RootElement.GetProperty("disk")));
    }

    // ---- text files: lines held by the graph ------------------------------------

    /// <summary>
    /// A graph holding one TextFile node for infra\main.bicep under the folder, with the
    /// given lines, built at the given time.
    /// </summary>
    private CodeGraph TextFileGraph(DateTimeOffset builtAt, params string[] lines)
    {
        var graph = new CodeGraph { BuiltAt = builtAt };
        var node = new GraphNode
        {
            Id = "text:infra/main.bicep", Type = NodeType.TextFile, Name = "main.bicep",
            FilePath = Path.Combine(_dir, "infra", "main.bicep"), LineStart = 1, LineEnd = lines.Length
        };
        node.SetProperty(TextSearch.TextFileLinesProperty, lines.ToList());
        graph.AddNode(node);
        return graph;
    }

    [Fact]
    public void Tool_GraphSection_AnswersATextFileLine_AndTheDiskCountsItInsteadOfRepeatingIt()
    {
        WriteFile(@"infra\main.bicep", "param location string\n\n  resource sa 'storage' = { location: location }\n");
        // Built after the write, with room for a coarse file clock: the file is fresh.
        var store = StoreOver(("g", TextFileGraph(DateTimeOffset.UtcNow.AddSeconds(5),
            "param location string", "", "  resource sa 'storage' = { location: location }")));

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("resource sa", _dir));
        var graph = doc.RootElement.GetProperty("graph");
        var disk = doc.RootElement.GetProperty("disk");

        var hit = Assert.Single(graph.GetProperty("hits").EnumerateArray().ToList());
        Assert.Equal("text:infra/main.bicep", hit.GetProperty("nodeId").GetString());
        Assert.Equal("TextFile", hit.GetProperty("nodeType").GetString());
        Assert.Equal("text", hit.GetProperty("matchedOn").GetString());
        Assert.Equal(Path.Combine("infra", "main.bicep"), hit.GetProperty("path").GetString());
        Assert.Equal(3, hit.GetProperty("line").GetInt32());
        Assert.Equal("resource sa 'storage' = { location: location }", hit.GetProperty("text").GetString());
        Assert.False(graph.TryGetProperty("staleTextFiles", out _));

        Assert.Equal(0, disk.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, disk.GetProperty("alreadyInGraph").GetInt32());
    }

    [Fact]
    public void Tool_ATextFileWrittenSinceTheBuild_IsAnsweredByTheDisk_AndCountedAsStale()
    {
        // The graph holds the line as it was; the file now says something else.
        WriteFile(@"infra\main.bicep", "param location string = 'westus'\n");
        var store = StoreOver(("g", TextFileGraph(DateTimeOffset.UtcNow.AddMinutes(-5), "param location string = 'eastus'")));

        using var doc = JsonDocument.Parse(new TextSearchTools(store).Grep("param location", _dir));
        var graph = doc.RootElement.GetProperty("graph");
        var disk = doc.RootElement.GetProperty("disk");

        Assert.Equal(0, graph.GetProperty("totalMatches").GetInt32());
        Assert.Equal(1, graph.GetProperty("staleTextFiles").GetInt32());

        var hit = Assert.Single(disk.GetProperty("hits").EnumerateArray().ToList());
        Assert.Equal("param location string = 'westus'", hit.GetProperty("text").GetString());
        Assert.Equal(0, disk.GetProperty("alreadyInGraph").GetInt32());
    }

    [Fact]
    public void Tool_RefusesAnInvalidRegex_SayingSo()
    {
        var message = Assert.Throws<McpException>(() => new TextSearchTools(new GraphStore()).Grep("([unclosed", _dir)).Message;

        Assert.Contains("not a valid .NET regular expression", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Tool_RefusesARelativeOrMissingFolder()
    {
        var tools = new TextSearchTools(new GraphStore());

        Assert.Contains("absolute path",
            Assert.Throws<McpException>(() => tools.Grep("x", @"src\app")).Message, StringComparison.Ordinal);
        Assert.Contains("does not exist",
            Assert.Throws<McpException>(() => tools.Grep("x", Path.Combine(_dir, "nope"))).Message, StringComparison.Ordinal);
    }
}
