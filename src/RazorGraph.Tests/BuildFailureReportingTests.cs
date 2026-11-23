namespace RazorGraph.Tests;

using System.Diagnostics;
using System.Text.Json;
using RazorGraph.Mcp;
using Xunit;

/// <summary>
/// A solution that does not build still gets a graph, of what did load and bind;
/// these pin that the build says so, by project and by error, root failures first.
///
/// The solution is written to a temp directory per run, because a broken project
/// cannot live in the shared fixtures without breaking every test that builds
/// them. Three projects: Broken compiles with an error of its own, Unloadable has
/// a malformed project file, and Dependent references Unloadable and fails only
/// because the types it uses never arrived.
/// </summary>
[Trait("Category", "Integration")]
public sealed class BuildFailureReportingTests : IAsyncLifetime
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rg-build-failure-" + Guid.NewGuid().ToString("N"));
    private string _response = "";
    private readonly GraphStore _store = new();

    private const string LibraryProject = """
        <Project Sdk="Microsoft.NET.Sdk">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            <Nullable>enable</Nullable>
          </PropertyGroup>
        </Project>
        """;

    public async Task InitializeAsync()
    {
        Write("Broken/Broken.csproj", LibraryProject);
        Write("Broken/Widget.cs", """
            namespace Broken;

            public class Widget
            {
                public int Size() => NoSuchMember;
            }
            """);

        Write("Unloadable/Unloadable.csproj", LibraryProject);
        Write("Unloadable/Part.cs", "namespace Unloadable; public class Part { }\n");

        Write("Dependent/Dependent.csproj", """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="..\Unloadable\Unloadable.csproj" />
              </ItemGroup>
            </Project>
            """);
        Write("Dependent/Holder.cs", "namespace Dependent; public class Holder { public Unloadable.Part? Part; }\n");

        Write("Failing.slnx", """
            <Solution>
              <Project Path="Broken/Broken.csproj" />
              <Project Path="Dependent/Dependent.csproj" />
              <Project Path="Unloadable/Unloadable.csproj" />
            </Solution>
            """);

        // Restored while every project file is valid, then one is broken: a load
        // failure is the thing under test, not a restore failure.
        Restore();
        Write("Unloadable/Unloadable.csproj", LibraryProject.Replace("</PropertyGroup>", "</PropertyGrop>"));

        _response = await new GraphStoreTools(_store)
            .BuildSolution(Path.Combine(_dir, "Failing.slnx"), graphId: "failing", excludeText: true);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    private void Write(string relative, string content)
    {
        var path = Path.Combine(_dir, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Restore()
    {
        var psi = new ProcessStartInfo("dotnet", "restore Failing.slnx")
        {
            WorkingDirectory = _dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true
        };
        using var process = Process.Start(psi)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet restore of the failing solution failed:\n{output}");
    }

    private List<string> Failures()
    {
        using var doc = JsonDocument.Parse(_response);
        return doc.RootElement.GetProperty("projectFailures").EnumerateArray().Select(e => e.GetString()!).ToList();
    }

    [Fact]
    public void BuildResponse_NamesTheProjectWithACompileError_AndItsFirstErrorAsFileLine()
    {
        var failures = Failures();

        Assert.Contains("Broken: 1 compile error(s), the graph holds what Roslyn could bind of it.", failures);
        Assert.Contains(failures, f =>
            f.StartsWith("Broken: Widget.cs:5: CS0103: The name 'NoSuchMember' does not exist", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildResponse_NamesTheProjectThatDidNotLoad_WithMsBuildsReason()
    {
        var failures = Failures();

        Assert.Contains("Unloadable: did not load, so none of its code is in the graph.", failures);
        Assert.Contains(failures, f => f.StartsWith("Unloadable: ", StringComparison.Ordinal) && f.Contains("PropertyGrop"));
        // Its empty compilation's own complaints would bury the reason, so it has none.
        Assert.DoesNotContain(failures, f => f.StartsWith("Unloadable: ", StringComparison.Ordinal) && f.Contains("CS"));
    }

    [Fact]
    public void BuildResponse_PutsRootFailuresFirst_AndSaysADependentMayOnlyBeCascading()
    {
        var failures = Failures();
        var header = failures.FindIndex(f => f.StartsWith("Dependent: ", StringComparison.Ordinal));

        Assert.True(header >= 0, "Dependent is not reported at all");
        Assert.Contains("it references Unloadable, which also failed", failures[header]);
        Assert.True(failures.FindLastIndex(f => f.StartsWith("Broken: ", StringComparison.Ordinal)) < header);
        Assert.True(failures.FindLastIndex(f => f.StartsWith("Unloadable: ", StringComparison.Ordinal)) < header);
    }

    [Fact]
    public void FailingBuild_StillProducesAGraph_OfWhatLoaded()
    {
        var graph = _store.Require("failing").Graph;

        Assert.Contains(graph.Nodes, n => n.Name == "Widget");
    }
}
