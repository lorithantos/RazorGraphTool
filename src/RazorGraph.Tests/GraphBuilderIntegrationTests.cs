namespace RazorGraph.Tests;

using System.Diagnostics;
using RazorGraph.Core.Graph;
using RazorGraph.Extractor;
using Xunit;

/// <summary>
/// End-to-end: builds a graph from the fixture Razor Pages app via MSBuildWorkspace.
/// The fixture is restored on first use; failures here should be loud, not skipped.
/// </summary>
[Trait("Category", "Integration")]
public class GraphBuilderIntegrationTests : IAsyncLifetime
{
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

            var root = RepoRoot();
            var fixtureDir = Path.Combine(root, "tests", "fixtures", "SampleApp");
            var projectPath = Path.Combine(fixtureDir, "SampleApp.csproj");
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

    [Fact]
    public void Builds_RazorPageNode_WithRouteTemplate()
    {
        var page = _graph!.Nodes.SingleOrDefault(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        Assert.NotNull(page);
        Assert.Equal("{id?}", page!.GetProperty<string>("routeTemplate"));
        Assert.Equal("IndexModel", page.GetProperty<string>("modelType"));
        Assert.Contains("Title", page.GetProperty<List<string>>("viewDataKeys") ?? new());
    }

    [Fact]
    public void Builds_PageServedByEdge_ToPageModel()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var served = _graph.Outgoing(page.Id).SingleOrDefault(e => e.Type == EdgeType.PageServedBy);

        Assert.NotNull(served);
        var pageModel = _graph.GetNode(served!.ToId);
        Assert.Equal(NodeType.PageModel, pageModel!.Type);
        Assert.Equal("IndexModel", pageModel.Name);
    }

    [Fact]
    public void Builds_InjectedIntoEdge_FromServiceToPageModel()
    {
        var pageModel = _graph!.Nodes.Single(n => n.Type == NodeType.PageModel && n.Name == "IndexModel");

        var injectors = _graph.Incoming(pageModel.Id)
            .Where(e => e.Type == EdgeType.InjectedInto)
            .Select(e => _graph.GetNode(e.FromId)!.Name)
            .ToList();

        Assert.Contains("IGreetingService", injectors);
    }

    [Fact]
    public void Builds_RendersPartialEdge_ToCard()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var partials = _graph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.RendersPartial)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("_Card", partials);
    }

    [Fact]
    public void Builds_ApiControllerAndViewModelNodes()
    {
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ViewModel && n.Name == "IndexViewModel");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ServiceInterface && n.Name == "IGreetingService");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");
    }

    [Fact]
    public void SymbolNodes_HaveRealLineNumbers()
    {
        var symbolNodes = _graph!.Nodes
            .Where(n => n.Type is NodeType.PageModel or NodeType.ApiController
                     or NodeType.ServiceInterface or NodeType.ServiceImplementation or NodeType.ViewModel)
            .ToList();

        Assert.NotEmpty(symbolNodes);
        Assert.All(symbolNodes, n =>
        {
            Assert.NotNull(n.LineStart);
            Assert.True(n.LineStart > 0, $"{n.Id} has LineStart {n.LineStart}");
            Assert.True(n.LineEnd >= n.LineStart, $"{n.Id} has LineEnd {n.LineEnd} < LineStart {n.LineStart}");
        });

        // The regression this guards: every symbol previously got the placeholder LineStart = 1.
        Assert.Contains(symbolNodes, n => n.LineStart > 1);
    }

    // ---- Attribute extraction ----------------------------------------------

    [Fact]
    public void Builds_DecoratedByEdge_WithTheArgumentAsWritten()
    {
        // [Route("api/greetings")] on GreetingsController.
        var controller = _graph!.Nodes.Single(n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        var route = _graph.Outgoing(controller.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.RouteAttribute");

        Assert.Equal(new List<object?> { "api/greetings" }, route.GetProperty<List<object?>>("args"));
        Assert.Equal("\"api/greetings\"", route.GetProperty<string>("source"));
        Assert.True(route.GetProperty<int>("line") > 0);
    }

    [Fact]
    public void Builds_DecoratedByEdge_OnMethodsAndProperties()
    {
        // [HttpGet("{name}")] on the action; [BindProperty] on the page model property.
        var get = _graph!.Outgoing("m:SampleApp.Api.GreetingsController.Get(string)")
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.HttpGetAttribute");
        Assert.Equal(new List<object?> { "{name}" }, get.GetProperty<List<object?>>("args"));

        var bind = _graph.Outgoing("prop:SampleApp.Pages.IndexModel.Name")
            .Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.BindPropertyAttribute", bind.ToId);
    }

    /// <summary>
    /// Absence of args must mean "no arguments", which only holds if an
    /// argument-less usage carries neither args nor source.
    /// </summary>
    [Fact]
    public void Builds_DecoratedByEdge_WithoutArgs_CarriesNoArgPayload()
    {
        var controller = _graph!.Nodes.Single(n => n.Type == NodeType.ApiController && n.Name == "GreetingsController");
        var apiController = _graph.Outgoing(controller.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == "ext:Microsoft.AspNetCore.Mvc.ApiControllerAttribute");

        Assert.False(apiController.Properties.ContainsKey("args"));
        Assert.False(apiController.Properties.ContainsKey("source"));
        Assert.False(apiController.Properties.ContainsKey("unresolvedArgs"));
    }

    // ---- Registers edges ---------------------------------------------------

    [Fact]
    public void Builds_RegistersEdge_FromAGenericAttributeTypeArgument()
    {
        // [RegisterService<IGreetingService>] on GreetingService: the framework
        // consults IGreetingService because of the annotation, no call site anywhere.
        var service = _graph!.Nodes.Single(n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");
        var registers = _graph.Outgoing(service.Id).Single(e => e.Type == EdgeType.Registers);

        Assert.Equal("IGreetingService", _graph.GetNode(registers.ToId)!.Name);

        // The attribute is declared in the fixture, so this must be its own
        // type: node, not a minted external one — and the DecoratedBy sibling
        // must join on (from, attribute, line).
        var attributeId = registers.GetProperty<string>("attribute");
        Assert.Equal("type:SampleApp.Infrastructure.RegisterServiceAttribute<TService>", attributeId);

        var decorated = _graph.Outgoing(service.Id)
            .Single(e => e.Type == EdgeType.DecoratedBy && e.ToId == attributeId);
        Assert.Equal(decorated.GetProperty<int>("line"), registers.GetProperty<int>("line"));
        Assert.Equal(new List<string> { "SampleApp.Services.IGreetingService" },
            decorated.GetProperty<List<string>>("typeArgs"));
    }

    [Fact]
    public void Builds_RegistersEdge_FromATypeofArgument()
    {
        // [ModelBinder(typeof(GreetingNameBinder))] on IndexViewModel.BoundName.
        var registers = _graph!.Outgoing("prop:SampleApp.Models.IndexViewModel.BoundName")
            .Single(e => e.Type == EdgeType.Registers);

        Assert.Equal("type:SampleApp.Infrastructure.GreetingNameBinder", registers.ToId);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.ModelBinderAttribute", registers.GetProperty<string>("attribute"));

        // The payload keeps the typeof spelling, so the edge and what a reader
        // sees in the arguments name the same type.
        var decorated = _graph.Outgoing("prop:SampleApp.Models.IndexViewModel.BoundName")
            .Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal(new List<object?> { "typeof(SampleApp.Infrastructure.GreetingNameBinder)" },
            decorated.GetProperty<List<object?>>("args"));
    }

    [Fact]
    public void Builds_NoRegistersEdge_ForATypeofPointingOutsideTheSolution()
    {
        // [TypeConverter(typeof(StringConverter))]: the fact survives in the
        // DecoratedBy payload; an edge to a node that could not say what it is
        // would add reachability without meaning.
        var outgoing = _graph!.Outgoing("prop:SampleApp.Models.IndexViewModel.ConvertedTitle").ToList();

        Assert.DoesNotContain(outgoing, e => e.Type == EdgeType.Registers);
        var decorated = outgoing.Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal(new List<object?> { "typeof(System.ComponentModel.StringConverter)" },
            decorated.GetProperty<List<object?>>("args"));
    }

    [Fact]
    public void Renders_ANullArrayArgument_AsAuthoredNull_NotAsAFailure()
    {
        // [RegisterService<GreetingNameBinder>(null)]: null where string[] is
        // expected still has TypedConstantKind.Array, and reading its Values
        // used to throw. An authored null is an argument, not a failure — the
        // failure signal is unresolvedArgs, and it must stay absent.
        var edge = _graph!.Outgoing("type:SampleApp.Infrastructure.GreetingNameBinder")
            .Single(e => e.Type == EdgeType.DecoratedBy);

        Assert.Equal(new List<object?> { null }, edge.GetProperty<List<object?>>("args"));
        Assert.False(edge.Properties.ContainsKey("unresolvedArgs"));
    }

    // ---- Parameter nodes ---------------------------------------------------

    [Fact]
    public void Builds_AParameterNode_OnlyForTheDecoratedParameter()
    {
        // [FromRoute] on Get's name parameter is the fixture's only decorated
        // parameter, so exactly one Parameter node may exist — the same
        // assertion also proves every undecorated parameter (GreetingService
        // .Greet's, the ctors') got none, which is what lets an absent param:
        // node mean "undecorated" rather than "unmodelled".
        var parameter = _graph!.Nodes.Single(n => n.Type == NodeType.Parameter);

        Assert.Equal("param:SampleApp.Api.GreetingsController.Get(string)#name", parameter.Id);
        Assert.Equal("name", parameter.Name);
        Assert.Equal(0, parameter.GetProperty<int>("ordinal"));
        Assert.Equal("string", parameter.GetProperty<string>("parameterType"));
    }

    [Fact]
    public void Builds_ContainsAndDecoratedBy_ForTheParameterNode()
    {
        const string parameterId = "param:SampleApp.Api.GreetingsController.Get(string)#name";

        // The parent method id is the text before the '#' — the shared
        // MethodBody is what guarantees that surgery always works.
        var contains = _graph!.Incoming(parameterId).Single(e => e.Type == EdgeType.Contains);
        Assert.Equal("m:SampleApp.Api.GreetingsController.Get(string)", contains.FromId);

        var decorated = _graph.Outgoing(parameterId).Single(e => e.Type == EdgeType.DecoratedBy);
        Assert.Equal("ext:Microsoft.AspNetCore.Mvc.FromRouteAttribute", decorated.ToId);
        Assert.True(decorated.GetProperty<int>("line") > 0);
    }

    // ---- Method-level extraction -------------------------------------------

    [Fact]
    public void Builds_MethodNodes_ContainedByTheirDeclaringType()
    {
        var service = _graph!.Nodes.Single(n => n.Type == NodeType.ServiceImplementation && n.Name == "GreetingService");

        var methods = _graph.Outgoing(service.Id)
            .Where(e => e.Type == EdgeType.Contains)
            .Select(e => _graph.GetNode(e.ToId)!)
            .ToList();

        Assert.NotEmpty(methods);
        Assert.All(methods, m => Assert.Equal(NodeType.Method, m.Type));
        Assert.All(methods, m => Assert.True(m.LineStart > 0, $"{m.Id} has no line number"));
    }

    [Fact]
    public void Builds_ClassNodes_ForTypesTheClassifierUsedToDrop()
    {
        // Program is neither page, controller, service, nor view model. Before
        // Class emission it was absent, and any call through it was unreachable.
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.Class);
    }

    [Fact]
    public void Builds_CallEdges_BetweenMethodNodesInThisCompilation()
    {
        var callEdges = _graph!.Edges.Where(e => e.Type == EdgeType.Calls).ToList();

        Assert.NotEmpty(callEdges);

        // Both endpoints must resolve, or "who calls this" returns a dangling id.
        Assert.All(callEdges, e =>
        {
            Assert.NotNull(_graph.GetNode(e.FromId));
            Assert.NotNull(_graph.GetNode(e.ToId));
        });
    }

    // ---- Client assets ------------------------------------------------------

    [Fact]
    public void Builds_JsAndCssNodes_AndSkipsVendorBundles()
    {
        Assert.Contains(_graph!.Nodes, n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");
        Assert.Contains(_graph.Nodes, n => n.Type == NodeType.CssFile && n.Name == "site.css");

        // wwwroot/lib is third-party; graphing it is cost without signal.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "jquery.js");

        // wwwroot/lib_npm is the same thing under the name the old substring
        // rule missed; it must be matched as a whole segment.
        Assert.DoesNotContain(_graph.Nodes, n => n.Name == "fakepkg.js");
    }

    [Fact]
    public void Builds_ReferencesEdge_FromPageToScript()
    {
        var page = _graph!.Nodes.Single(n => n.Type == NodeType.RazorPage && n.Name == "Index");

        var referenced = _graph.Outgoing(page.Id)
            .Where(e => e.Type == EdgeType.References)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("site.js", referenced);
    }

    [Fact]
    public void Builds_ViewDataReadByEdge_ForKeyRenderedByAPartial()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var edge = _graph.Incoming(js.Id).SingleOrDefault(e => e.Type == EdgeType.ViewDataReadBy);

        // data-greeting is rendered by _Card, not by Index. The coupling is only
        // visible if partial markup counts as part of the page's DOM.
        Assert.NotNull(edge);
        var keys = Assert.IsType<List<string>>(edge!.Properties["dataKeys"]);
        Assert.Contains("greeting", keys);
    }

    [Fact]
    public void Reports_OnlyTheGenuinelyUnboundKey()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var unbound = js.GetProperty<List<string>>("unboundDataKeys") ?? new();

        // missing-key: read, never rendered anywhere -> a real broken contract.
        Assert.Contains("missing-key", unbound);
        // greeting: rendered by the partial.
        Assert.DoesNotContain("greeting", unbound);
        // state: rendered as a constant, so the attribute does exist in the DOM.
        Assert.DoesNotContain("state", unbound);
        // client-owned: the script assigns it, so the server owes nothing.
        Assert.DoesNotContain("client-owned", unbound);
    }

    [Fact]
    public void Builds_DomSelectedByEdge_ForIdsThePageCompositionRenders()
    {
        var edge = _graph!.Edges.Single(e => e.Type == EdgeType.DomSelectedBy);
        var ids = edge.GetProperty<List<string>>("ids");

        // Single-project page ids keep the OS path separator; only solution
        // builds normalize (see SolutionGraphIntegrationTests).
        Assert.Equal($"page:Pages{Path.DirectorySeparatorChar}Index.cshtml", edge.FromId);
        Assert.Equal("js:wwwroot/js/site.js", edge.ToId);
        // "Name" comes from asp-for on Index itself; "card-title" arrives
        // through the _Card partial — the composed DOM, not just the page.
        Assert.Equal(new[] { "Name", "card-title" }, ids);
    }

    [Fact]
    public void Reports_OnlyTheGenuinelyUnboundSelectorId()
    {
        var script = _graph!.Nodes.Single(n => n.Id == "js:wwwroot/js/site.js");
        var unbound = script.GetProperty<List<string>>("unboundSelectorIds");

        // cart-count: selected, never rendered. popup-host: selected but
        // self-created. Name/card-title: bound. Only the first is a defect.
        Assert.Equal(new[] { "cart-count" }, unbound);
    }

    [Fact]
    public void Builds_CallsEdge_FromScriptToApiController()
    {
        var js = _graph!.Nodes.Single(n => n.Type == NodeType.JavaScriptFile && n.Name == "site.js");

        var called = _graph.Outgoing(js.Id)
            .Where(e => e.Type == EdgeType.Calls)
            .Select(e => _graph.GetNode(e.ToId)!.Name)
            .ToList();

        Assert.Contains("GreetingsController", called);
    }
}
