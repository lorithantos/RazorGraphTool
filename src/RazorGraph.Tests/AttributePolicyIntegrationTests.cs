namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using RazorGraph.Extractor.Attributes;
using Xunit;

/// <summary>
/// End-to-end proof that the attribute policy is configuration: the SAME
/// fixture built with an override policy produces a different graph, with no
/// change to any binary. One build, shared across the assertions.
/// </summary>
[Trait("Category", "Integration")]
public class AttributePolicyIntegrationTests : IAsyncLifetime
{
    private static readonly SemaphoreSlim BuildGate = new(1, 1);
    private static CodeGraph? _graph;

    public async Task InitializeAsync()
    {
        await BuildGate.WaitAsync();
        try
        {
            if (_graph != null) return;

            var root = RepoRoot();
            var projectPath = Path.Combine(root, "tests", "fixtures", "SampleApp", "SampleApp.csproj");
            Assert.True(File.Exists(projectPath), $"Fixture project missing: {projectPath}");

            var policyPath = Path.Combine(Path.GetTempPath(), $"attribute-policy-suppress-{Guid.NewGuid():N}.json");
            await File.WriteAllTextAsync(policyPath, """
                {
                  ".comment": ["Withholds RouteAttribute's payload; everything else inherited."],
                  "suppressArgumentsFor": { "names": ["Microsoft.AspNetCore.Mvc.RouteAttribute"] }
                }
                """);

            await using var builder = new GraphBuilder { AttributePolicy = AttributePolicy.LoadFile(policyPath) };
            _graph = await builder.BuildFromProjectAsync(projectPath);
        }
        finally
        {
            BuildGate.Release();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "RazorGraphTool.slnx")))
            dir = dir.Parent;
        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate RazorGraphTool.slnx above the test directory.");
    }

    [Fact]
    public void SuppressedAttribute_KeepsItsEdge_AndLosesItsPayload()
    {
        var controller = _graph!.Nodes.Single(n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        var route = _graph.Outgoing(controller.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.RouteAttribute");

        // The edge and its line survive — suppression withholds data, never facts.
        Assert.True(route.GetProperty<int>("line") > 0);
        Assert.False(route.Properties.ContainsKey("args"));
        Assert.False(route.Properties.ContainsKey("source"));
    }

    [Fact]
    public void Suppression_IsScopedToTheNamedAttribute()
    {
        // [HttpGet("{name}")] is not in the suppress set, so its payload stays.
        var get = _graph!.Outgoing("m:SampleApp.Api.GreetingsController.Get(string)")
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.HttpGetAttribute");

        Assert.Equal(new List<object?> { "{name}" }, get.GetProperty<List<object?>>("args"));
    }
}
