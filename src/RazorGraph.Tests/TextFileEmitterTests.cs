namespace RazorGraph.Tests;

using RazorGraph.Core.Graph;
using RazorGraph.Core.Query;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// The TextFile population over a temp folder outside any git tree, so the files are
/// listed by the directory walk and every case can be written on the spot -- a binary
/// and a 3 MB file included, which no committed fixture should carry.
/// </summary>
public class TextFileEmitterTests : IDisposable
{
    private readonly string _dir =
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"rg-text-{Guid.NewGuid():N}")).FullName;

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

    private (CodeGraph Graph, TextFileEmitter Emitter) Emit(params string[] alreadyFiles)
    {
        var graph = new CodeGraph();
        var emitter = new TextFileEmitter(graph);
        emitter.AddTextFiles(_dir, new HashSet<string>(alreadyFiles, StringComparer.OrdinalIgnoreCase));
        return (graph, emitter);
    }

    private static List<string> TextIds(CodeGraph graph) =>
        graph.NodesOfType(NodeType.TextFile).Select(n => n.Id).OrderBy(id => id, StringComparer.Ordinal).ToList();

    [Fact]
    public void Population_LeavesOutFilesTheGraphAlreadyModels_AndKeepsTheRest()
    {
        WriteFile("Program.cs", "class P { }\n");
        WriteFile(@"Pages\Index.cshtml", "<p>hi</p>\n");
        WriteFile(@"Shared\Card.razor", "<div />\n");
        WriteFile(@"Views\Main.xaml", "<Window />\n");
        var script = WriteFile(@"wwwroot\js\site.js", "let x = 1;\n");
        var style = WriteFile(@"wwwroot\css\site.css", "p { }\n");
        WriteFile(@"wwwroot\js\loose.js", "let y = 2;\n");
        WriteFile(@"wwwroot\lib\pkg\README.md", "# vendored\n");
        WriteFile(@"wwwroot\css\bootstrap.min.css.map", "{}\n");
        WriteFile("App.csproj", "<Project />\n");
        WriteFile(@"infra\main.bicep", "param x string\n");
        WriteFile(@"docs\notes.md", "# notes\n");

        // site.js and site.css stand in for JavaScriptFile and CssFile nodes; loose.js
        // has none, so it is text like any other file.
        var (graph, _) = Emit(script, style);

        Assert.Equal(
            ["text:App.csproj", "text:docs/notes.md", "text:infra/main.bicep", "text:wwwroot/js/loose.js"],
            TextIds(graph));
    }

    [Fact]
    public void Population_InAGitTree_StillLeavesOutVendorDirectories_AndSaysHowMany()
    {
        // Outside git the walk never enters lib/; inside one, git lists whatever is
        // committed or untracked there -- a committed wwwroot/lib is the usual case --
        // so the population's own vendor rule is the only thing keeping it out.
        WriteFile(@"wwwroot\lib\pkg\README.md", "# vendored\n");
        WriteFile(@"tools\vendor\pkg\package.json", "{}\n");
        WriteFile("App.csproj", "<Project />\n");
        RunGit("init", "--quiet");

        var (graph, emitter) = Emit();

        Assert.Equal(["text:App.csproj"], TextIds(graph));
        Assert.Contains(emitter.SkipSummaries, s => s.StartsWith("2 file(s) under vendor", StringComparison.Ordinal));
    }

    private void RunGit(params string[] args)
    {
        var info = new System.Diagnostics.ProcessStartInfo("git") { WorkingDirectory = _dir, UseShellExecute = false };
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = System.Diagnostics.Process.Start(info)!;
        Assert.True(process.WaitForExit(10_000));
        Assert.Equal(0, process.ExitCode);
    }

    [Fact]
    public void Population_SkipsABinaryAndAFileOver2MB_AndReportsBoth()
    {
        File.WriteAllBytes(Path.Combine(_dir, "logo.png"), [0x89, 0x50, 0x4E, 0x47, 0x00, 0x01]);
        WriteFile("dump.json", new string('x', (int)SourceFiles.MaxFileBytes + 1));
        WriteFile("kept.json", "{}\n");

        var (graph, emitter) = Emit();

        Assert.Equal(["text:kept.json"], TextIds(graph));
        Assert.Contains(emitter.SkipSummaries, s => s.Contains("binary", StringComparison.Ordinal) && s.Contains("logo.png", StringComparison.Ordinal));
        Assert.Contains(emitter.SkipSummaries, s => s.Contains("over 2 MB", StringComparison.Ordinal) && s.Contains("dump.json", StringComparison.Ordinal));
    }

    [Fact]
    public void Node_CarriesItsRelativeIdAndAbsolutePath_AndEveryLineInOrder()
    {
        var path = WriteFile(@"infra\main.bicep", "param name string\r\n\r\nresource sa 'x' = {}\n");

        var (graph, emitter) = Emit();
        var node = Assert.Single(graph.NodesOfType(NodeType.TextFile));

        Assert.Equal("text:infra/main.bicep", node.Id);
        Assert.Equal("main.bicep", node.Name);
        Assert.Equal(path, node.FilePath);
        Assert.Equal(1, node.LineStart);
        Assert.Equal(3, node.LineEnd);
        Assert.Equal(["param name string", "", "resource sa 'x' = {}"], node.GetProperty<List<string>>("lines"));
        Assert.Equal(".bicep", node.GetProperty<string>("extension"));
        Assert.Equal((int)new FileInfo(path).Length, node.GetProperty<int>("sizeBytes"));
        Assert.Null(node.GetProperty<string>("project"));
        Assert.Empty(emitter.SkipSummaries);
    }
}
